// Wires the sections to the page. The only module that touches `document`; it builds every element with
// createElement and textContent and never parses HTML, so nothing the API returns can add markup.

import { createClient } from './api.mjs';
import { priceChart } from './chart.mjs';
import { ctPerKwh, deviceNames, kw, kwh, label, percent, shortId } from './format.mjs';
import { collectPages } from './paging.mjs';
import { confirmDraft, fieldMessages, goalRequest, planView, savingsView } from './plan.mjs';
import { followCommands, isFinal } from './poll.mjs';
import { createSubmission } from './submit.mjs';
import { addDays, berlinDate, berlinDayRange, formatDay, formatTime, formatWhen, utcIso } from './time.mjs';

const ZONE_ID = 'DE-LU';
const SVG_NS = 'http://www.w3.org/2000/svg';
const GOAL_FIELDS = ['vehicleDeviceId', 'chargerDeviceId', 'targetSocPercent', 'departureAt'];
const FIELD_INPUTS = { vehicleDeviceId: 'goal-vehicle', chargerDeviceId: 'goal-charger', targetSocPercent: 'goal-target', departureAt: 'goal-departure' };
const GOALS_PER_PAGE = 20;

const $ = (id) => document.getElementById(id);

const state = {
  devices: [],
  names: new Map(),
  goals: [],
  goalsCursor: null,
  selectedGoalId: null,
  follow: null,
  goalSubmission: createSubmission(),
  confirmSubmission: createSubmission(),
};

const api = createClient({
  fetch: globalThis.fetch.bind(globalThis),
  storage: sessionStore(),
  onUnauthorized: () => showKeyForm('That key was not accepted. Enter a valid API key.'),
});

// ---------------------------------------------------------------- elements

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  return dress(node, props, children);
}

function svg(tag, props = {}, ...children) {
  const node = document.createElementNS(SVG_NS, tag);
  return dress(node, props, children);
}

function dress(node, props, children) {
  for (const [name, value] of Object.entries(props)) {
    if (value === null || value === undefined || value === false) {
      continue;
    }
    if (name === 'text') {
      node.textContent = String(value);
    } else if (name.startsWith('on') && typeof value === 'function') {
      node.addEventListener(name.slice(2), value);
    } else {
      node.setAttribute(name, value === true ? '' : String(value));
    }
  }
  for (const child of children.flat()) {
    if (child !== null && child !== undefined && child !== false) {
      node.append(child);
    }
  }
  return node;
}

function badge(value) {
  return el('span', { class: `badge ${label(value).toLowerCase().replaceAll(' ', '-')}`, text: label(value) });
}

function table(headers, rows) {
  return el('div', { class: 'table-wrap' },
    el('table', {},
      el('thead', {}, el('tr', {}, headers.map((h) => el('th', { scope: 'col', class: h.number ? 'number' : null, text: h.text })))),
      el('tbody', {}, rows)));
}

function cell(text, number = false) {
  return el('td', { class: number ? 'number' : null, text });
}

function figures(items) {
  return el('dl', { class: 'figures' }, items.map((i) => el('div', { class: 'figure' }, el('dt', { text: i.label }), el('dd', { text: i.text }))));
}

function problemBox(error) {
  const box = el('div', { class: 'problem' }, el('p', {}, el('strong', { text: error?.title ?? 'Something went wrong' })));
  if (error?.detail) {
    box.append(el('p', { text: error.detail }));
  }
  return box;
}

// ---------------------------------------------------------------- sections

function setStatus(section, content) {
  const status = $(`${section}-status`);
  status.replaceChildren(...(content ? [content] : []));
}

function loading(section) {
  setStatus(section, el('p', { class: 'muted', text: 'Loading…' }));
}

function failed(section, error) {
  // A rejected key already brought the key form back.
  if (error?.status === 401) {
    setStatus(section, null);
    return;
  }
  setStatus(section, problemBox(error));
}

async function loadAll() {
  await Promise.allSettled([loadDevices(), loadPrices(), loadGoals(), loadSavings()]);
}

// Devices -----------------------------------------------------------------

async function loadDevices() {
  loading('devices');
  try {
    state.devices = await collectPages((cursor) => api.request('GET', '/v1/devices', { query: { limit: 200, cursor } }));
    state.names = deviceNames(state.devices);
    setStatus('devices', null);
    renderDevices();
  } catch (error) {
    failed('devices', error);
  }
  renderGoalFormChoices();
  if (state.goals.length > 0) {
    renderGoals();
  }
}

function renderDevices() {
  const content = $('devices-content');
  if (state.devices.length === 0) {
    content.replaceChildren(el('p', { class: 'muted', text: 'No devices yet. Start the charger simulator or link a vehicle account, then refresh.' }));
    return;
  }
  content.replaceChildren(table(
    [{ text: 'Device' }, { text: 'Status' }, { text: 'Last seen' }, { text: 'Power', number: true }, { text: 'Energy', number: true }, { text: 'Charge', number: true }],
    state.devices.map((d) => el('tr', {},
      // The id stays out of sight; hovering the name shows it for support or API calls.
      el('td', { title: d.id },
        el('div', { class: 'device-name', text: state.names.get(d.id)?.name ?? label(d.type) }),
        el('div', { class: 'muted', text: state.names.get(d.id)?.detail ?? '' })),
      el('td', {}, badge(d.status)),
      cell(d.lastSeenAt ? formatWhen(d.lastSeenAt) : 'Never'),
      // An offline device draws nothing we know of; its last power would read as current.
      cell(d.lastReading && d.status !== 'Offline' ? kw(d.lastReading.powerW) : '–', true),
      cell(d.lastReading ? kwh(d.lastReading.energyWh) : '–', true),
      cell(d.lastReading ? percent(d.lastReading.socPercent) : '–', true)))));
}

function deviceName(id) {
  return state.names.get(id)?.name ?? `Device ${shortId(id)}`;
}

// Prices ------------------------------------------------------------------

async function loadPrices() {
  setStatus('prices', null);
  const today = berlinDate(new Date());
  const days = [{ date: today, name: 'Today' }, { date: addDays(today, 1), name: 'Tomorrow' }];
  const blocks = days.map((day) => {
    const block = el('div', { class: 'price-day' }, el('h3', { text: `${day.name}, ${formatDay(day.date)}` }), el('p', { class: 'muted', text: 'Loading…' }));
    return { day, block };
  });
  $('prices-content').replaceChildren(...blocks.map((b) => b.block));

  await Promise.all(blocks.map(async ({ day, block }) => {
    const range = berlinDayRange(day.date);
    try {
      const slots = await collectPages((cursor) => api.request('GET', '/v1/prices', { query: { zone: ZONE_ID, from: range.from, to: range.to, limit: 200, cursor } }));
      block.replaceChildren(block.firstChild, ...priceDay(day, slots));
    } catch (error) {
      if (error?.status !== 401) {
        block.replaceChildren(block.firstChild, problemBox(error));
      }
    }
  }));
}

function priceDay(day, slots) {
  if (slots.length === 0) {
    return [el('p', { class: 'notice', text: `${day.name}'s prices are not published yet. Day-ahead prices usually appear after 13:00.` })];
  }
  const cheapest = slots.reduce((a, b) => (b.priceEurPerMwh < a.priceEurPerMwh ? b : a));
  const dearest = slots.reduce((a, b) => (b.priceEurPerMwh > a.priceEurPerMwh ? b : a));
  return [
    el('p', { class: 'muted', text: `Cheapest ${formatTime(cheapest.start)}–${formatTime(cheapest.end)} at ${ctPerKwh(cheapest.priceEurPerMwh)} · most expensive ${formatTime(dearest.start)}–${formatTime(dearest.end)} at ${ctPerKwh(dearest.priceEurPerMwh)}` }),
    chart(priceChart(slots), `Day-ahead prices for ${formatDay(day.date)} in ct/kWh`),
    priceTable(slots),
  ];
}

function priceTable(slots, planned = new Set()) {
  return el('details', {},
    el('summary', { text: `Show all ${slots.length} quarter hours as a table` }),
    table(
      [{ text: 'Time' }, { text: 'Price', number: true }, ...(planned.size ? [{ text: 'Plan' }] : [])],
      slots.map((s) => el('tr', {},
        cell(`${formatTime(s.start)}–${formatTime(s.end)}`),
        cell(ctPerKwh(s.priceEurPerMwh), true),
        ...(planned.size ? [cell(planned.has(new Date(s.start).getTime()) ? 'Charging' : '')] : [])))));
}

function chart(model, caption) {
  const { width, height, padding } = model;
  const node = svg('svg', { viewBox: `0 0 ${width} ${height}`, class: 'chart', role: 'img', 'aria-label': caption },
    svg('title', { text: caption }),
    model.yTicks.map((t) => svg('text', { x: padding.left - 6, y: t.y + 4, 'text-anchor': 'end', text: t.text })),
    model.bars.map((b) => svg('rect', { class: `bar${b.planned ? ' planned' : ''}${b.negative ? ' negative' : ''}`, x: b.x, y: b.y, width: b.width, height: b.height },
      svg('title', { text: b.title }))),
    svg('line', { class: 'axis', x1: padding.left, x2: width - padding.right, y1: model.zeroY, y2: model.zeroY }),
    model.hourLabels.map((l) => svg('text', { x: l.x, y: height - 8, text: l.text })));
  const legend = el('div', { class: 'legend' },
    el('span', {}, el('span', { class: 'swatch' }), `price (${model.unit})`),
    model.bars.some((b) => b.planned) ? el('span', {}, el('span', { class: 'swatch planned' }), 'planned charging') : null,
    model.bars.some((b) => b.negative) ? el('span', {}, el('span', { class: 'swatch negative' }), 'negative price') : null);
  return el('figure', {}, node, legend);
}

// Goals -------------------------------------------------------------------

async function loadGoals({ more = false } = {}) {
  if (!more) {
    loading('goals');
  }
  try {
    const page = await api.request('GET', '/v1/goals', { query: { limit: GOALS_PER_PAGE, cursor: more ? state.goalsCursor : undefined } });
    state.goals = more ? [...state.goals, ...page.data] : page.data;
    state.goalsCursor = page.nextCursor;
    setStatus('goals', null);
    renderGoals();
  } catch (error) {
    failed('goals', error);
  }
}

function renderGoals() {
  const content = $('goals-content');
  if (state.goals.length === 0) {
    content.replaceChildren(el('p', { class: 'muted', text: 'No goals yet. Set one below.' }));
    return;
  }
  const list = el('ul', { class: 'goal-list' }, state.goals.map((goal) => {
    const plan = goal.currentPlan;
    return el('li', { class: goal.id === state.selectedGoalId ? 'selected' : null },
      el('div', {},
        el('div', {}, el('strong', { text: `Departs ${formatWhen(goal.departureAt, { midSentence: true })}` }), ` · charge to ${percent(goal.targetSocPercent)}`),
        el('div', { class: 'muted', text: `${deviceName(goal.vehicleDeviceId)} on ${deviceName(goal.chargerDeviceId)}${plan.version > 1 ? ` · plan version ${plan.version}` : ''}` })),
      el('div', { class: 'actions' },
        badge(plan.status),
        plan.status === 'Draft' && plan.expiresAt ? el('span', { class: 'muted', text: `expires ${formatWhen(plan.expiresAt, { midSentence: true })}` }) : null,
        el('button', { type: 'button', class: 'secondary', text: 'Show plan', onclick: () => showPlan(goal) })));
  }));
  const more = state.goalsCursor
    ? el('div', { class: 'actions' }, el('button', { type: 'button', class: 'secondary', text: 'More goals', onclick: () => loadGoals({ more: true }) }))
    : null;
  content.replaceChildren(list, ...(more ? [more] : []));
}

function renderGoalFormChoices() {
  fillSelect($('goal-vehicle'), state.devices.filter((d) => d.type === 'Vehicle'), 'No vehicle yet');
  fillSelect($('goal-charger'), state.devices.filter((d) => d.type === 'Charger'), 'No charger yet');
  const departure = $('goal-departure');
  if (!departure.value) {
    departure.value = `${addDays(berlinDate(new Date()), 1)}T07:00`;
  }
}

function fillSelect(select, devices, empty) {
  const chosen = select.value;
  select.replaceChildren(...(devices.length === 0
    ? [el('option', { value: '', text: empty })]
    : devices.map((d) => el('option', { value: d.id, text: state.names.get(d.id)?.choice ?? label(d.type) }))));
  if (devices.some((d) => d.id === chosen)) {
    select.value = chosen;
  }
}

function showFieldErrors(byField) {
  for (const field of GOAL_FIELDS) {
    const input = $(FIELD_INPUTS[field]);
    const message = byField[field]?.join(' ') ?? '';
    $(`${FIELD_INPUTS[field]}-error`).textContent = message;
    if (message) {
      input.setAttribute('aria-invalid', 'true');
    } else {
      input.removeAttribute('aria-invalid');
    }
  }
}

async function submitGoal(event) {
  event.preventDefault();
  const message = $('goal-message');
  message.replaceChildren();
  const form = $('goal-form');
  const { body, errors } = goalRequest({
    vehicleDeviceId: form.elements.vehicleDeviceId.value,
    chargerDeviceId: form.elements.chargerDeviceId.value,
    targetSocPercent: form.elements.targetSocPercent.value,
    departureLocal: form.elements.departureLocal.value,
  });
  showFieldErrors(errors);
  if (!body) {
    return;
  }

  const button = $('goal-submit');
  button.disabled = true;
  try {
    const created = await state.goalSubmission.run(body, (idempotencyKey) => api.request('POST', '/v1/goals', { body, idempotencyKey }));
    message.replaceChildren(el('p', { class: 'ok', text: 'Goal set. Its draft plan is shown below; confirm it before it expires.' }));
    const goal = { ...created.goal, currentPlan: { id: created.plan.id, version: created.plan.version, status: created.plan.status, expiresAt: created.plan.expiresAt } };
    stopFollowing();
    renderPlan(created.plan, goal);
    loadGoals();
  } catch (error) {
    if (error?.status === 401) {
      return;
    }
    const { byField, general } = fieldMessages(error?.errors, GOAL_FIELDS);
    showFieldErrors(byField);
    const box = problemBox(error);
    general.forEach((m) => box.append(el('p', { text: m })));
    if (error?.unreachable) {
      box.append(el('p', { text: 'Submit again to retry; the same request is sent, so the goal is never set twice.' }));
    }
    message.replaceChildren(box);
  } finally {
    button.disabled = false;
  }
}

// Plan --------------------------------------------------------------------

async function showPlan(goal) {
  stopFollowing();
  state.selectedGoalId = goal.id;
  renderGoals();
  loading('plan');
  try {
    const plan = await api.request('GET', `/v1/goals/${encodeURIComponent(goal.id)}/plan`);
    setStatus('plan', null);
    renderPlan(plan, goal);
  } catch (error) {
    failed('plan', error);
  }
  $('plan').scrollIntoView({ behavior: 'smooth', block: 'start' });
}

function renderPlan(plan, goal) {
  state.selectedGoalId = goal.id;
  const view = planView(plan);
  const confirmMessage = el('div', { class: 'message', role: 'status' });
  const commands = el('div', {});
  const chartArea = el('div', {}, view.slots.length ? el('p', { class: 'muted', text: 'Loading prices…' }) : null);

  const actions = el('div', { class: 'actions' });
  if (view.canConfirm) {
    const button = el('button', { type: 'button', text: 'Confirm plan' });
    button.addEventListener('click', () => confirmPlan(plan, goal, button, confirmMessage));
    actions.append(button, el('span', { class: 'muted', text: 'Confirm to have the charger start and stop at these times.' }));
  }

  $('plan-content').replaceChildren(
    el('div', { class: 'plan-meta' },
      el('h3', { text: view.heading }),
      badge(plan.status),
      el('span', { class: 'muted', text: view.version }),
      view.expires ? el('span', { class: 'muted', text: view.expires }) : null),
    el('p', { class: 'muted', text: `Charge ${deviceName(goal.vehicleDeviceId)} to ${percent(goal.targetSocPercent)} by ${formatWhen(goal.departureAt, { midSentence: true })}, on ${deviceName(goal.chargerDeviceId)}.` }),
    figures(view.amounts),
    ...view.notices.map((n) => el('p', { class: 'notice', text: n })),
    ...view.proposals.map((p) => el('p', { class: 'notice', text: `${p} (applied when this draft is confirmed)` })),
    view.runs.length
      ? el('div', {},
        el('p', { text: view.summary }),
        table([{ text: 'When' }, { text: 'Duration' }, { text: 'Power', number: true }], view.runs.map((r) => el('tr', {}, cell(r.when), cell(r.duration), cell(r.power, true)))))
      : el('p', { class: 'muted', text: 'No charging is planned.' }),
    actions,
    confirmMessage,
    commands,
    chartArea);

  if (view.slots.length) {
    loadPlanChart(plan, goal, chartArea);
  }
  return { confirmMessage, commands };
}

async function loadPlanChart(plan, goal, area) {
  // From now (or the first slot, when that is earlier), so the dearer slots the plan skipped show too.
  const quarter = 900_000;
  const from = utcIso(Math.min(new Date(plan.slots[0].start).getTime(), Math.floor(Date.now() / quarter) * quarter));
  const to = utcIso(goal.departureAt);
  if (new Date(to) <= new Date(from)) {
    area.replaceChildren();
    return;
  }
  try {
    const prices = await collectPages((cursor) => api.request('GET', '/v1/prices', { query: { zone: ZONE_ID, from, to, limit: 200, cursor } }));
    if (prices.length === 0) {
      area.replaceChildren(el('p', { class: 'notice', text: 'No published prices for this period yet.' }));
      return;
    }
    const planned = new Set(plan.slots.map((s) => new Date(s.start).getTime()));
    area.replaceChildren(
      chart(priceChart(prices, { planned: plan.slots.map((s) => s.start), labelEveryHours: prices.length <= 48 ? 1 : 3 }), 'Prices until departure, with the planned slots marked'),
      priceTable(prices, planned));
  } catch (error) {
    area.replaceChildren(...(error?.status === 401 ? [] : [problemBox(error)]));
  }
}

async function confirmPlan(plan, goal, button, message) {
  button.disabled = true;
  message.replaceChildren(el('p', { class: 'muted', text: 'Confirming…' }));
  const outcome = await confirmDraft({ api, submission: state.confirmSubmission, planId: plan.id });
  button.disabled = false;
  if (outcome.kind === 'needs-execute') {
    message.replaceChildren(el('p', { class: 'problem', text: outcome.message }));
    return;
  }
  if (outcome.kind === 'problem') {
    message.replaceChildren(problemBox(outcome));
    return;
  }

  const shown = renderPlan(outcome.plan, { ...goal, currentPlan: { ...goal.currentPlan, status: outcome.plan.status } });
  const count = outcome.commands.length;
  shown.confirmMessage.replaceChildren(el('p', { class: 'ok', text: `Plan confirmed. ${count} start and stop ${count === 1 ? 'command is' : 'commands are'} scheduled for the charger.` }));
  followConfirmed(outcome.commands, shown.commands);
  loadGoals();
  loadSavings();
}

function followConfirmed(commands, area) {
  stopFollowing();
  const rows = new Map();
  // Rows are named by what the command does; the id is only in the hover title.
  const body = commands.map((c, i) => {
    const row = el('tr', {}, el('td', { title: c.id, text: `Command ${i + 1}` }), el('td', {}, badge(c.state)), cell('0', true), cell(''));
    rows.set(c.id, row);
    return row;
  });
  area.replaceChildren(
    el('h3', { text: 'Commands' }),
    el('p', { class: 'muted', text: 'Each command waits for its slot, so most stay Pending until then. This list updates while the tab is open, until every command is done.' }),
    table([{ text: 'Command' }, { text: 'State' }, { text: 'Attempts', number: true }, { text: 'Updated' }], body));

  state.follow = followCommands({
    commands,
    fetchCommand: (id) => api.request('GET', `/v1/commands/${encodeURIComponent(id)}`),
    isVisible: () => document.visibilityState === 'visible',
    onUpdate: (command) => {
      const row = rows.get(command.id);
      if (!row) {
        return;
      }
      const last = command.history?.at?.(-1);
      row.replaceChildren(
        el('td', { title: command.id, text: label(command.type) }),
        el('td', {}, badge(command.state)),
        cell(String(command.attempts ?? 0), true),
        cell(last ? `${formatWhen(last.at)}${last.reason ? ` · ${last.reason}` : ''}${isFinal(command.state) ? '' : ' · checking'}` : ''));
    },
    onError: (id, error) => {
      const row = rows.get(id);
      if (row && error?.status !== 401) {
        row.lastChild.textContent = `${error?.title ?? 'Refresh failed'}; retrying`;
      }
    },
  });
}

function stopFollowing() {
  state.follow?.stop();
  state.follow = null;
}

// Savings -----------------------------------------------------------------

async function loadSavings() {
  loading('savings');
  const to = new Date();
  const from = new Date(to.getTime() - 30 * 24 * 3_600_000);
  try {
    const savings = await api.request('GET', '/v1/savings', { query: { from: utcIso(from), to: utcIso(to) } });
    setStatus('savings', null);
    $('savings-content').replaceChildren(
      figures(savingsView(savings)),
      el('p', { class: 'muted', text: 'Counts goals that departed in the last 30 days with a confirmed plan and complete prices. Exchange prices only, before grid fees and taxes.' }));
  } catch (error) {
    failed('savings', error);
  }
}

// Key ---------------------------------------------------------------------

function showKeyForm(message) {
  stopFollowing();
  $('dashboard').hidden = true;
  $('key-actions').hidden = true;
  $('key-section').hidden = false;
  const input = $('api-key');
  input.value = '';
  $('key-message').textContent = message ?? '';
  input.focus();
}

function showDashboard() {
  $('key-section').hidden = true;
  $('dashboard').hidden = false;
  $('key-actions').hidden = false;
  loadAll();
}

function sessionStore() {
  try {
    const storage = window.sessionStorage;
    storage.getItem('cheaphours.probe');
    return storage;
  } catch {
    // Storage blocked: keep the key in memory for this page only.
    const items = new Map();
    return {
      getItem: (name) => items.get(name) ?? null,
      setItem: (name, value) => items.set(name, String(value)),
      removeItem: (name) => items.delete(name),
    };
  }
}

function start() {
  $('key-form').addEventListener('submit', (event) => {
    event.preventDefault();
    try {
      api.setKey($('api-key').value);
    } catch {
      $('key-message').textContent = 'Enter an API key.';
      return;
    }
    $('api-key').value = '';
    showDashboard();
  });
  $('forget-key').addEventListener('click', () => {
    api.forgetKey();
    for (const id of ['devices-content', 'prices-content', 'goals-content', 'savings-content', 'goal-message']) {
      $(id).replaceChildren();
    }
    $('plan-content').replaceChildren(el('p', { class: 'muted', text: 'Choose a goal to see its plan.' }));
    state.devices = [];
    state.names = new Map();
    state.goals = [];
    state.selectedGoalId = null;
    showKeyForm('Key forgotten. It is no longer stored in this tab.');
  });
  $('refresh').addEventListener('click', () => loadAll());
  $('goal-form').addEventListener('submit', submitGoal);

  if (api.hasKey()) {
    showDashboard();
  } else {
    showKeyForm(null);
  }
}

start();
