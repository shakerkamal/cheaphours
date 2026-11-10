import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ctPerKwh, deviceNames, eur, kw, kwh, label, percent, shortId } from '../site/js/format.mjs';

test('amounts in EUR with two decimals', () => {
  assert.equal(eur(1.2), '€1.20');
  assert.equal(eur(4.2), '€4.20');
  assert.equal(eur(0), '€0.00');
  assert.equal(eur(-0.03), '-€0.03');
  assert.equal(eur(null), '–');
});

test('power in kW and energy in kWh', () => {
  assert.equal(kw(11000), '11.0 kW');
  assert.equal(kw(3680), '3.7 kW');
  assert.equal(kwh(12345), '12.35 kWh');
  assert.equal(kwh(0), '0.00 kWh');
  assert.equal(kw(undefined), '–');
});

test('charge in percent', () => {
  assert.equal(percent(45), '45%');
  assert.equal(percent(40.5), '40.5%');
  assert.equal(percent(null), '–');
});

test('day-ahead prices in ct/kWh, negative allowed', () => {
  assert.equal(ctPerKwh(85.2), '8.52 ct/kWh');
  assert.equal(ctPerKwh(-31), '-3.10 ct/kWh');
  assert.equal(ctPerKwh(-0.01), '0.00 ct/kWh');
  assert.equal(ctPerKwh(null), '–');
});

test('devices get a readable name and the facts that tell them apart, never their id', () => {
  const names = deviceNames([
    { id: 'dev_01M5MJ4QPWE27BTNN7KSN5K6A1', type: 'Charger', providerId: 'ocpp', maxPowerW: 11000 },
    { id: 'dev_01M5MJ4QPWE27BTNN7KSN5K6A2', type: 'Vehicle', providerId: 'carcloud', batteryCapacityWh: 60000 },
    { id: 'dev_01M5MJ4QPWE27BTNN7KSN5K6A3', type: 'HeatPump', providerId: 'heatpumpcloud' },
    { id: 'dev_01M5MJ4QPWE27BTNN7KSN5K6A4', type: 'Charger', providerId: 'someday' },
  ]);

  assert.deepEqual([...names.values()], [
    { name: 'Charger 1', detail: '11.0 kW max · via OCPP', choice: 'Charger 1 (11.0 kW max)' },
    { name: 'Vehicle', detail: '60 kWh battery · via car cloud', choice: 'Vehicle (60 kWh battery)' },
    { name: 'Heat pump', detail: 'via heat-pump cloud', choice: 'Heat pump' },
    { name: 'Charger 2', detail: 'via someday', choice: 'Charger 2' },
  ]);
});

test('an id is cut to its last four characters', () => {
  assert.equal(shortId('dev_01M5MJ4QPWE27BTNN7KSN5K6A1'), '…K6A1');
  assert.equal(shortId('dev_1'), 'dev_1');
});

test('enum values as words', () => {
  assert.equal(label('DeadLettered'), 'Dead lettered');
  assert.equal(label('Confirmed'), 'Confirmed');
  assert.equal(label('ReadTelemetry'), 'Read telemetry');
  assert.equal(label(undefined), '–');
});
