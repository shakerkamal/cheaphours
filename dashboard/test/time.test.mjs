import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  addDays,
  berlinDate,
  berlinDayRange,
  berlinLocalToUtcIso,
  berlinToUtc,
  formatDateTime,
  formatDuration,
  formatTime,
  formatWhen,
  hoursBetween,
  utcIso,
  utcToBerlinLocal,
} from '../site/js/time.mjs';

test('summer time: 05:00 UTC shows 07:00 on 1 July 2026', () => {
  assert.equal(formatDateTime('2026-07-01T05:00:00Z'), '1 July 2026, 07:00');
  assert.equal(formatTime('2026-07-01T05:00:00Z'), '07:00');
});

test('winter time: 07:00 Berlin on 2 November 2026 is sent as 06:00 UTC', () => {
  assert.equal(berlinLocalToUtcIso('2026-11-02T07:00'), '2026-11-02T06:00:00Z');
  assert.equal(utcToBerlinLocal('2026-11-02T06:00:00Z'), '2026-11-02T07:00');
});

test('an ordinary day is 24 hours from Berlin midnight', () => {
  const range = berlinDayRange('2026-11-02');
  assert.deepEqual(range, { from: '2026-11-01T23:00:00Z', to: '2026-11-02T23:00:00Z' });
  assert.equal(hoursBetween(range.from, range.to), 24);
});

test('the spring change day is 23 hours (92 slots)', () => {
  const range = berlinDayRange('2026-03-29');
  assert.deepEqual(range, { from: '2026-03-28T23:00:00Z', to: '2026-03-29T22:00:00Z' });
  assert.equal(hoursBetween(range.from, range.to) * 4, 92);
});

test('the autumn change day is 25 hours (100 slots)', () => {
  const range = berlinDayRange('2026-10-25');
  assert.deepEqual(range, { from: '2026-10-24T22:00:00Z', to: '2026-10-25T23:00:00Z' });
  assert.equal(hoursBetween(range.from, range.to) * 4, 100);
});

test('a time the spring change skips moves forward by an hour', () => {
  // 02:30 does not exist on 29 March 2026; it becomes 03:30 summer time.
  assert.equal(utcIso(berlinToUtc({ year: 2026, month: 3, day: 29, hour: 2, minute: 30 })), '2026-03-29T01:30:00Z');
  assert.equal(formatTime('2026-03-29T01:30:00Z'), '03:30');
});

test('a time the autumn change repeats takes the first (summer-time) instant', () => {
  // 02:30 happens twice on 25 October 2026: 00:30Z (summer) and 01:30Z (winter).
  assert.equal(utcIso(berlinToUtc({ year: 2026, month: 10, day: 25, hour: 2, minute: 30 })), '2026-10-25T00:30:00Z');
  assert.equal(utcIso(berlinToUtc({ year: 2026, month: 10, day: 25, hour: 3, minute: 30 })), '2026-10-25T02:30:00Z');
});

test('the Berlin date of an instant and calendar steps across months', () => {
  assert.equal(berlinDate('2026-10-24T22:30:00Z'), '2026-10-25');
  assert.equal(berlinDate('2026-10-24T21:59:00Z'), '2026-10-24');
  assert.equal(addDays('2026-12-31', 1), '2027-01-01');
  assert.equal(addDays('2026-03-01', -1), '2026-02-28');
});

test('unreadable local input gives null', () => {
  assert.equal(berlinLocalToUtcIso(''), null);
  assert.equal(berlinLocalToUtcIso('tomorrow'), null);
});

test('times near now say today, tomorrow or yesterday; others give the weekday and date', () => {
  const now = new Date('2026-11-09T10:00:00Z');

  assert.equal(formatWhen('2026-11-09T22:30:00Z', { now }), 'Today, 23:30');
  assert.equal(formatWhen('2026-11-09T23:30:00Z', { now }), 'Tomorrow, 00:30');
  assert.equal(formatWhen('2026-11-08T06:00:00Z', { now }), 'Yesterday, 07:00');
  assert.equal(formatWhen('2026-11-12T06:00:00Z', { now }), 'Thu 12 November, 07:00');
  assert.equal(formatWhen('2027-01-04T06:00:00Z', { now }), 'Mon 4 January 2027, 07:00');
  assert.equal(formatWhen('2026-11-10T06:00:00Z', { now, midSentence: true }), 'tomorrow, 07:00');
});

test('durations in hours and minutes', () => {
  assert.equal(formatDuration(15), '15 min');
  assert.equal(formatDuration(60), '1 h');
  assert.equal(formatDuration(135), '2 h 15 min');
});
