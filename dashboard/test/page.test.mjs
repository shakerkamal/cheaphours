import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import { priceChart } from '../site/js/chart.mjs';
import { createClient } from '../site/js/api.mjs';
import { confirmDraft, fieldMessages, goalRequest, NEEDS_EXECUTE, planView, savingsView, TARGET_MISSED } from '../site/js/plan.mjs';
import { createSubmission } from '../site/js/submit.mjs';
import { berlinDayRange } from '../site/js/time.mjs';
import { MemoryStorage, fakeFetch, json, problem } from './fakes.mjs';

const site = new URL('../site/', import.meta.url);

test('every script, stylesheet and icon the page loads has a relative URL', async () => {
  const html = await readFile(new URL('index.html', site), 'utf8');
  const urls = [...html.matchAll(/\b(?:src|href)\s*=\s*"([^"]*)"/g)].map((m) => m[1]);

  assert.ok(urls.includes('js/app.mjs') && urls.includes('styles.css'));
  for (const url of urls) {
    assert.ok(!/^[a-z][a-z0-9+.-]*:/i.test(url) && !url.startsWith('//') && !url.startsWith('/'), `${url} is not relative`);
  }
  assert.ok(!/<style|style\s*=|<script(?![^>]*\bsrc=)/i.test(html), 'inline styles or scripts would break the CSP');
});

test('only app.mjs touches the document, and nothing sets innerHTML', async () => {
  const dir = new URL('js/', site);
  for (const name of await readdir(dir)) {
    const source = await readFile(new URL(name, dir), 'utf8');
    assert.ok(!/innerHTML|outerHTML|insertAdjacentHTML|document\.write/.test(source), `${name} writes markup`);
    if (name !== 'app.mjs') {
      assert.ok(!/\bdocument\b|\bwindow\b/.test(source), `${name} touches the DOM`);
    }
  }
});

function day(date, price = (i) => 50 + i) {
  const { from, to } = berlinDayRange(date);
  const slots = [];
  for (let t = new Date(from).getTime(), i = 0; t < new Date(to).getTime(); t += 900_000, i++) {
    slots.push({ zone: 'DE-LU', start: new Date(t).toISOString(), end: new Date(t + 900_000).toISOString(), priceEurPerMwh: price(i) });
  }
  return slots;
}

test('a 92-slot day draws 92 bars with Berlin hour labels', () => {
  const slots = day('2026-03-29');
  assert.equal(slots.length, 92);

  const model = priceChart(slots);

  assert.equal(model.bars.length, 92);
  assert.deepEqual(model.hourLabels.map((l) => l.text), ['00:00', '03:00', '06:00', '09:00', '12:00', '15:00', '18:00', '21:00']);
  assert.equal(model.bars[8].title, '03:00–03:15 · 5.80 ct/kWh');
  assert.equal(model.unit, 'ct/kWh');
  assert.deepEqual(model.yTicks.map((t) => t.text), ['14.1', '0']);
  assert.ok(model.bars.every((b, i) => i === 0 || b.x > model.bars[i - 1].x));
});

test('a 100-slot day labels the repeated hour once', () => {
  const model = priceChart(day('2026-10-25'));

  assert.equal(model.bars.length, 100);
  assert.equal(model.hourLabels.length, 8);
});

test('planned slots are marked and negative prices hang below the zero line', () => {
  const slots = day('2026-11-02', (i) => (i === 10 ? -20 : 40));
  const model = priceChart(slots, { planned: [slots[3].start, slots[10].start] });

  assert.deepEqual(model.bars.filter((b) => b.planned).map((b) => b.start), [slots[3].start, slots[10].start]);
  const negative = model.bars[10];
  assert.ok(negative.negative && negative.y === model.zeroY && negative.height > 0);
  assert.ok(Math.abs(model.bars[3].y + model.bars[3].height - model.zeroY) < 0.02);
  assert.match(model.bars[3].title, /planned$/);
});

const draft = {
  id: 'plan_1',
  goalId: 'goal_1',
  version: 1,
  status: 'Draft',
  slots: [
    { start: '2026-11-03T01:00:00Z', end: '2026-11-03T01:15:00Z', powerW: 11000 },
    { start: '2026-11-03T01:15:00Z', end: '2026-11-03T01:30:00Z', powerW: 11000 },
    { start: '2026-11-03T03:00:00Z', end: '2026-11-03T03:15:00Z', powerW: 11000 },
  ],
  expectedCostEur: 1.2,
  arrivalCostEur: 2.5,
  savingsEur: 1.3,
  targetMissed: false,
  pricesComplete: true,
  expiresAt: '2026-11-02T17:10:00Z',
};

const evening = new Date('2026-11-02T17:00:00Z');

test('a draft plan shows its three slots, the three amounts and its expiry', () => {
  const view = planView(draft, { now: evening });

  assert.equal(view.slots.length, 3);
  assert.deepEqual(view.slots.map((s) => s.time), ['02:00–02:15', '02:15–02:30', '04:00–04:15']);
  assert.deepEqual(view.amounts.map((a) => a.text), ['€1.20', '€2.50', '€1.30']);
  assert.equal(view.expires, 'Expires today, 18:10');
  assert.equal(view.version, 'Version 1');
  assert.equal(view.canConfirm, true);
  assert.deepEqual(view.notices, []);
});

test('consecutive slots read as charging runs, with the total time', () => {
  const view = planView(draft, { now: evening });

  assert.deepEqual(view.runs, [
    { when: 'Tomorrow, 02:00–02:30', duration: '30 min', power: '11.0 kW' },
    { when: 'Tomorrow, 04:00–04:15', duration: '15 min', power: '11.0 kW' },
  ]);
  assert.equal(view.summary, 'Charges for 45 min in 2 runs.');
  assert.equal(planView({ ...draft, slots: draft.slots.slice(0, 1) }, { now: evening }).summary, 'Charges for 15 min in 1 run.');
});

test('a missed target and incomplete prices are said; only a draft can be confirmed', () => {
  const view = planView({ ...draft, status: 'Confirmed', targetMissed: true, pricesComplete: false, expiresAt: null });

  assert.equal(view.notices[0], TARGET_MISSED);
  assert.equal(view.notices.length, 2);
  assert.equal(view.canConfirm, false);
  assert.equal(view.expires, null);
  assert.equal(planView({ ...draft, status: 'Expired' }).canConfirm, false);
});

test('savings show the sessions and three amounts in EUR with two decimals', () => {
  const view = savingsView({ from: '2026-10-10T00:00:00Z', to: '2026-11-09T00:00:00Z', sessions: 3, plannedCostEur: 2.1, arrivalCostEur: 6.3, savingsEur: 4.2 });

  assert.deepEqual(view.map((f) => f.text), ['3', '€2.10', '€6.30', '€4.20']);
});

test('the goal form sends UTC and checks its fields first', () => {
  const ok = goalRequest({ vehicleDeviceId: 'dev_car', chargerDeviceId: 'dev_box', targetSocPercent: '80', departureLocal: '2026-11-02T07:00' });
  assert.deepEqual(ok.body, { vehicleDeviceId: 'dev_car', chargerDeviceId: 'dev_box', targetSocPercent: 80, departureAt: '2026-11-02T06:00:00Z' });

  const bad = goalRequest({ vehicleDeviceId: '', chargerDeviceId: 'dev_box', targetSocPercent: '120', departureLocal: '' });
  assert.equal(bad.body, null);
  assert.deepEqual(Object.keys(bad.errors).sort(), ['departureAt', 'targetSocPercent', 'vehicleDeviceId']);
});

test('a refused goal shows its field errors next to their fields', () => {
  const { byField, general } = fieldMessages({ targetSocPercent: ['Must be between 1 and 100.'], body: ['Unreadable.'] }, ['targetSocPercent', 'departureAt']);

  assert.deepEqual(byField, { targetSocPercent: ['Must be between 1 and 100.'] });
  assert.deepEqual(general, ['body: Unreadable.']);
});

function clientWith(fetch) {
  const client = createClient({ fetch, storage: new MemoryStorage() });
  client.setKey('ch_demo_read');
  return client;
}

test('confirming with a read key says an execute key is needed', async () => {
  const fetch = fakeFetch(problem(403, 'Forbidden', 'This API key lacks the execute scope.'));

  const outcome = await confirmDraft({ api: clientWith(fetch), submission: createSubmission(), planId: 'plan_1' });

  assert.deepEqual(outcome, { kind: 'needs-execute', message: NEEDS_EXECUTE });
  assert.equal(fetch.calls[0].method, 'POST');
  assert.equal(fetch.calls[0].url, '/v1/plans/plan_1/confirm');
  assert.ok(fetch.calls[0].headers['Idempotency-Key']);
});

test('a confirmed plan returns its commands', async () => {
  const fetch = fakeFetch(json(202, { plan: { ...draft, status: 'Confirmed', expiresAt: null }, commands: [{ id: 'cmd_1', state: 'Pending' }, { id: 'cmd_2', state: 'Pending' }] }));

  const outcome = await confirmDraft({ api: clientWith(fetch), submission: createSubmission(), planId: 'plan_1' });

  assert.equal(outcome.kind, 'confirmed');
  assert.equal(outcome.plan.status, 'Confirmed');
  assert.deepEqual(outcome.commands.map((c) => c.id), ['cmd_1', 'cmd_2']);
});

test('an expired draft passes the problem on', async () => {
  const fetch = fakeFetch(problem(410, 'Plan expired', 'The draft expired; change the goal or wait for new prices to get a new draft.'));

  const outcome = await confirmDraft({ api: clientWith(fetch), submission: createSubmission(), planId: 'plan_1' });

  assert.equal(outcome.kind, 'problem');
  assert.equal(outcome.title, 'Plan expired');
  assert.match(outcome.detail, /expired/);
});
