import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createClient } from '../site/js/api.mjs';
import { collectPages } from '../site/js/paging.mjs';
import { followCommands } from '../site/js/poll.mjs';
import { createSubmission } from '../site/js/submit.mjs';
import { FakeTimers, MemoryStorage, fakeFetch, json, problem } from './fakes.mjs';

function clientWith(fetch) {
  const client = createClient({ fetch, storage: new MemoryStorage() });
  client.setKey('k');
  return client;
}

test('two pages of devices are joined', async () => {
  const fetch = fakeFetch(
    json(200, { data: [{ id: 'dev_1' }, { id: 'dev_2' }], nextCursor: 'abc' }),
    json(200, { data: [{ id: 'dev_3' }], nextCursor: null }),
  );
  const client = clientWith(fetch);

  const devices = await collectPages((cursor) => client.request('GET', '/v1/devices', { query: { limit: 200, cursor } }));

  assert.deepEqual(devices.map((d) => d.id), ['dev_1', 'dev_2', 'dev_3']);
  assert.deepEqual(fetch.calls.map((c) => c.url), ['/v1/devices?limit=200', '/v1/devices?limit=200&cursor=abc']);
});

test('a cursor that never ends is stopped', async () => {
  await assert.rejects(collectPages(async () => ({ data: [], nextCursor: 'again' }), { maxPages: 3 }));
});

test('a retry after a network failure sends the same Idempotency-Key', async () => {
  const fetch = fakeFetch(new TypeError('Failed to fetch'), json(201, { goal: { id: 'goal_1' } }));
  const client = clientWith(fetch);
  let n = 0;
  const submission = createSubmission({ newKey: () => `key-${++n}` });
  const body = { targetSocPercent: 80 };
  const send = (idempotencyKey) => client.request('POST', '/v1/goals', { body, idempotencyKey });

  await assert.rejects(submission.run(body, send), (error) => error.unreachable);
  await submission.run(body, send);

  assert.deepEqual(fetch.calls.map((c) => c.headers['Idempotency-Key']), ['key-1', 'key-1']);
  assert.equal(submission.pendingKey, null);
});

test('any HTTP response ends the submission, so the next one gets a new key', async () => {
  const fetch = fakeFetch(problem(422, 'Unprocessable', 'Departure is in the past.'), json(201, {}));
  const client = clientWith(fetch);
  let n = 0;
  const submission = createSubmission({ newKey: () => `key-${++n}` });
  const body = { targetSocPercent: 80 };
  const send = (idempotencyKey) => client.request('POST', '/v1/goals', { body, idempotencyKey });

  await assert.rejects(submission.run(body, send), (error) => error.status === 422);
  await submission.run(body, send);

  assert.deepEqual(fetch.calls.map((c) => c.headers['Idempotency-Key']), ['key-1', 'key-2']);
});

test('a changed body after a network failure is a new submission', async () => {
  const fetch = fakeFetch(new TypeError('Failed to fetch'), json(201, {}));
  const client = clientWith(fetch);
  let n = 0;
  const submission = createSubmission({ newKey: () => `key-${++n}` });
  const send = (body) => (idempotencyKey) => client.request('POST', '/v1/goals', { body, idempotencyKey });

  await assert.rejects(submission.run({ targetSocPercent: 80 }, send({ targetSocPercent: 80 })));
  await submission.run({ targetSocPercent: 90 }, send({ targetSocPercent: 90 }));

  assert.deepEqual(fetch.calls.map((c) => c.headers['Idempotency-Key']), ['key-1', 'key-2']);
});

test('a command read as Published, Sent, Confirmed is requested three times and never again', async () => {
  const timers = new FakeTimers();
  const states = ['Published', 'Sent', 'Confirmed'];
  const requested = [];
  const shown = [];
  followCommands({
    commands: [{ id: 'cmd_1', state: 'Pending' }],
    fetchCommand: async (id) => {
      requested.push(timers.now);
      return { id, state: states.shift() };
    },
    onUpdate: (c) => shown.push(c.state),
    timers,
    now: () => timers.now,
  });

  await timers.advance(10 * 60_000);

  assert.deepEqual(shown, ['Published', 'Sent', 'Confirmed']);
  assert.deepEqual(requested, [2_000, 4_000, 6_000]);
  assert.equal(timers.pending, 0);
});

test('every 2 s for the first minute, then every 30 s', async () => {
  const timers = new FakeTimers();
  const requested = [];
  followCommands({
    commands: [{ id: 'cmd_1', state: 'Pending' }],
    fetchCommand: async (id) => {
      requested.push(timers.now);
      return { id, state: 'Pending' };
    },
    onUpdate: () => {},
    timers,
    now: () => timers.now,
  });

  await timers.advance(120_000);

  const fast = requested.filter((t) => t <= 60_000);
  assert.equal(fast.length, 30);
  assert.ok(fast.every((t, i) => t === (i + 1) * 2_000));
  assert.deepEqual(requested.filter((t) => t > 60_000), [90_000, 120_000]);
});

test('nothing is requested while the tab is hidden', async () => {
  const timers = new FakeTimers();
  let visible = false;
  let requests = 0;
  followCommands({
    commands: [{ id: 'cmd_1', state: 'Sent' }],
    fetchCommand: async (id) => {
      requests++;
      return { id, state: 'Confirmed' };
    },
    onUpdate: () => {},
    timers,
    now: () => timers.now,
    isVisible: () => visible,
  });

  await timers.advance(30_000);
  assert.equal(requests, 0);

  visible = true;
  await timers.advance(2_000);
  assert.equal(requests, 1);
  await timers.advance(60_000);
  assert.equal(requests, 1);
});

test('commands already final are not followed, and a failed read keeps the command followed', async () => {
  const timers = new FakeTimers();
  const errors = [];
  let calls = 0;
  const follow = followCommands({
    commands: [{ id: 'cmd_done', state: 'Cancelled' }, { id: 'cmd_open', state: 'Pending' }],
    fetchCommand: async (id) => {
      calls++;
      if (calls === 1) {
        throw new Error('API unreachable');
      }
      return { id, state: 'Rejected' };
    },
    onUpdate: () => {},
    onError: (id) => errors.push(id),
    timers,
    now: () => timers.now,
  });

  assert.deepEqual(follow.open(), ['cmd_open']);
  await timers.advance(10_000);
  assert.deepEqual(errors, ['cmd_open']);
  assert.deepEqual(follow.open(), []);
  assert.equal(calls, 2);
});
