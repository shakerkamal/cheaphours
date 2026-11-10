// What the goal and plan sections show and send, kept apart from the DOM so Node can test it.

import { eur, kw, label } from './format.mjs';
import { berlinLocalToUtcIso, formatDuration, formatTime, formatWhen } from './time.mjs';

export const TARGET_MISSED = 'The target cannot be reached before departure: even charging in every slot left falls short.';
export const PRICES_INCOMPLETE = 'Some slots have no published price yet; the costs leave them out.';
export const NEEDS_EXECUTE = 'Confirming a plan needs an API key with the execute scope. This key can only read; the plan stays a draft.';

/** The plan section's content for a plan from GET /v1/goals/{id}/plan or a confirmation. */
export function planView(plan, { now = new Date() } = {}) {
  const notices = [];
  if (plan.targetMissed) {
    notices.push(TARGET_MISSED);
  }
  if (!plan.pricesComplete) {
    notices.push(PRICES_INCOMPLETE);
  }

  const proposals = [];
  if (plan.proposedTargetSocPercent != null) {
    proposals.push(`Proposed target: ${plan.proposedTargetSocPercent}%`);
  }
  if (plan.proposedDepartureAt) {
    proposals.push(`Proposed departure: ${formatWhen(plan.proposedDepartureAt, { now, midSentence: true })}`);
  }

  const isDraft = plan.status === 'Draft';
  const slots = plan.slots ?? [];
  const runs = chargingRuns(slots);
  const totalMinutes = runs.reduce((sum, r) => sum + r.minutes, 0);
  return {
    heading: 'Charging plan',
    version: `Version ${plan.version}`,
    status: label(plan.status),
    canConfirm: isDraft,
    expires: (isDraft || plan.status === 'Expired') && plan.expiresAt
      ? `${isDraft ? 'Expires' : 'Expired'} ${formatWhen(plan.expiresAt, { now, midSentence: true })}`
      : null,
    summary: runs.length > 0 ? `Charges for ${formatDuration(totalMinutes)} in ${runs.length} ${runs.length === 1 ? 'run' : 'runs'}.` : null,
    runs: runs.map((r) => ({ when: `${formatWhen(r.start, { now })}–${formatTime(r.end)}`, duration: formatDuration(r.minutes), power: kw(r.powerW) })),
    amounts: [
      { label: 'Expected cost', text: eur(plan.expectedCostEur) },
      { label: 'Charging on arrival', text: eur(plan.arrivalCostEur) },
      { label: 'Savings', text: eur(plan.savingsEur) },
    ],
    notices,
    proposals,
    slots: slots.map((s) => ({ start: s.start, time: `${formatTime(s.start)}–${formatTime(s.end)}`, power: kw(s.powerW) })),
  };
}

/** Slots that follow each other at the same power, joined into one run of charging. */
function chargingRuns(slots) {
  const runs = [];
  for (const slot of slots) {
    const last = runs.at(-1);
    if (last && last.end === new Date(slot.start).getTime() && last.powerW === slot.powerW) {
      last.end = new Date(slot.end).getTime();
    } else {
      runs.push({ start: new Date(slot.start).getTime(), end: new Date(slot.end).getTime(), powerW: slot.powerW });
    }
  }
  return runs.map((r) => ({ ...r, minutes: (r.end - r.start) / 60_000 }));
}

/** The savings section's figures for a response from GET /v1/savings. */
export function savingsView(savings) {
  return [
    { label: 'Charging sessions', text: String(savings.sessions) },
    { label: 'Planned cost', text: eur(savings.plannedCostEur) },
    { label: 'Charging on arrival', text: eur(savings.arrivalCostEur) },
    { label: 'Savings', text: eur(savings.savingsEur) },
  ];
}

/**
 * Builds the body of POST /v1/goals from the form's values, or the fields to fix first.
 * @param {{ vehicleDeviceId?: string, chargerDeviceId?: string, targetSocPercent?: string, departureLocal?: string }} values
 */
export function goalRequest(values) {
  const errors = {};
  if (!values.vehicleDeviceId) {
    errors.vehicleDeviceId = ['Choose a vehicle.'];
  }
  if (!values.chargerDeviceId) {
    errors.chargerDeviceId = ['Choose a charger.'];
  }
  const target = Number(values.targetSocPercent);
  if (!Number.isInteger(target) || target < 1 || target > 100) {
    errors.targetSocPercent = ['A whole number from 1 to 100.'];
  }
  const departureAt = berlinLocalToUtcIso(values.departureLocal);
  if (!departureAt) {
    errors.departureAt = ['Choose a departure time.'];
  }
  if (Object.keys(errors).length > 0) {
    return { body: null, errors };
  }
  return {
    body: { vehicleDeviceId: values.vehicleDeviceId, chargerDeviceId: values.chargerDeviceId, targetSocPercent: target, departureAt },
    errors: {},
  };
}

/** Splits a problem's `errors` into the form's fields and the rest. */
export function fieldMessages(errors, fields) {
  const byField = {};
  const general = [];
  for (const [name, messages] of Object.entries(errors ?? {})) {
    const list = Array.isArray(messages) ? messages.map(String) : [String(messages)];
    if (fields.includes(name)) {
      byField[name] = list;
    } else {
      general.push(...list.map((m) => `${name}: ${m}`));
    }
  }
  return { byField, general };
}

/**
 * Confirms a draft. A 403 says an execute key is needed; any other problem is passed on as it came.
 * @returns {Promise<{ kind: 'confirmed', plan: object, commands: object[] } | { kind: 'needs-execute', message: string } | { kind: 'problem', title: string, detail: string | null, unreachable: boolean }>}
 */
export async function confirmDraft({ api, submission, planId }) {
  try {
    const result = await submission.run({ planId }, (idempotencyKey) =>
      api.request('POST', `/v1/plans/${encodeURIComponent(planId)}/confirm`, { idempotencyKey }));
    return { kind: 'confirmed', plan: result.plan, commands: result.commands ?? [] };
  } catch (error) {
    if (error?.status === 403) {
      return { kind: 'needs-execute', message: NEEDS_EXECUTE };
    }
    return { kind: 'problem', title: error?.title ?? 'Request failed', detail: error?.detail ?? null, unreachable: Boolean(error?.unreachable) };
  }
}
