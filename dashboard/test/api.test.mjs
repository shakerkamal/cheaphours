import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import { createClient, ApiError, KEY_STORAGE_ITEM } from '../site/js/api.mjs';
import { MemoryStorage, fakeFetch, json, problem } from './fakes.mjs';

const page = { data: [], nextCursor: null };

function setup(...responders) {
  const storage = new MemoryStorage();
  const local = new MemoryStorage();
  const fetch = fakeFetch(...responders);
  let unauthorized = 0;
  const client = createClient({ fetch, storage, onUnauthorized: () => unauthorized++ });
  return { client, storage, local, fetch, unauthorized: () => unauthorized };
}

test('the key is sent as X-Api-Key and never in the URL', async () => {
  const { client, fetch } = setup(json(200, page));
  client.setKey('ch_demo_read');

  await client.request('GET', '/v1/devices', { query: { limit: 200, cursor: undefined } });

  const [call] = fetch.calls;
  assert.equal(call.headers['X-Api-Key'], 'ch_demo_read');
  assert.equal(call.url, '/v1/devices?limit=200');
  assert.ok(!call.url.includes('ch_demo_read'));
  assert.equal(call.method, 'GET');
  assert.equal(call.body, undefined);
});

test('the key is kept in session storage only, and forgetting it removes it', () => {
  const { client, storage, local } = setup(json(200, page));

  client.setKey('  ch_demo_read  ');
  assert.equal(storage.getItem(KEY_STORAGE_ITEM), 'ch_demo_read');
  assert.equal(local.size, 0);
  assert.deepEqual(local.writes, []);

  client.forgetKey();
  assert.equal(storage.getItem(KEY_STORAGE_ITEM), null);
  assert.equal(client.hasKey(), false);
});

test('no module but api.mjs touches localStorage or calls fetch', async () => {
  const dir = new URL('../site/js/', import.meta.url);
  for (const name of await readdir(dir)) {
    const source = await readFile(new URL(name, dir), 'utf8');
    assert.ok(!/localStorage/.test(source), `${name} uses localStorage`);
    assert.ok(!/console\.(log|info|debug)/.test(source), `${name} logs to the console`);
    if (name !== 'api.mjs') {
      assert.ok(!/\bfetch\s*\(/.test(source), `${name} calls fetch outside api.mjs`);
    }
  }
});

test('a 401 removes the key and asks for one again', async () => {
  const { client, storage, unauthorized } = setup(problem(401, 'Unauthorized', 'The API key is not valid.'));
  client.setKey('ch_revoked');

  await assert.rejects(client.request('GET', '/v1/devices'), (error) => error instanceof ApiError && error.status === 401);

  assert.equal(storage.getItem(KEY_STORAGE_ITEM), null);
  assert.equal(unauthorized(), 1);
});

test('without a key nothing is sent and the key is asked for', async () => {
  const { client, fetch, unauthorized } = setup(json(200, page));

  await assert.rejects(client.request('GET', '/v1/devices'), ApiError);

  assert.equal(fetch.calls.length, 0);
  assert.equal(unauthorized(), 1);
});

test('problem details are parsed into status, title, detail and errors', async () => {
  const { client } = setup(problem(422, 'Unprocessable', 'The target is below the charge.', { targetSocPercent: ['Must be above 40.'] }));
  client.setKey('k');

  await assert.rejects(client.request('POST', '/v1/goals', { body: {}, idempotencyKey: 'id-1' }), (error) => {
    assert.equal(error.status, 422);
    assert.equal(error.title, 'Unprocessable');
    assert.equal(error.detail, 'The target is below the charge.');
    assert.deepEqual(error.errors, { targetSocPercent: ['Must be above 40.'] });
    assert.equal(error.unreachable, false);
    return true;
  });
});

test('a POST carries its JSON body and idempotency key', async () => {
  const { client, fetch } = setup(json(201, { goal: {}, plan: {} }));
  client.setKey('k');

  const body = await client.request('POST', '/v1/goals', { body: { targetSocPercent: 80 }, idempotencyKey: 'id-1' });

  assert.deepEqual(body, { goal: {}, plan: {} });
  assert.equal(fetch.calls[0].headers['Idempotency-Key'], 'id-1');
  assert.equal(fetch.calls[0].headers['Content-Type'], 'application/json');
  assert.equal(fetch.calls[0].body, '{"targetSocPercent":80}');
});

test('no response means API unreachable', async () => {
  const { client } = setup(new TypeError('Failed to fetch'));
  client.setKey('k');

  await assert.rejects(client.request('GET', '/v1/prices'), (error) => error.unreachable && error.title === 'API unreachable');
});

test('the web server answering 502 without problem details is API unreachable too', async () => {
  const { client } = setup(new Response('<html>Bad Gateway</html>', { status: 502, headers: { 'Content-Type': 'text/html' } }));
  client.setKey('k');

  await assert.rejects(client.request('GET', '/v1/prices'), (error) => error.unreachable && error.status === 0);
});

test('only /v1 paths are called', async () => {
  const { client, fetch } = setup(json(200, page));
  client.setKey('k');

  await assert.rejects(client.request('GET', 'https://elsewhere.example/v1/devices'));
  assert.equal(fetch.calls.length, 0);
});
