// Europe/Berlin for display, UTC on the wire. No date library: Intl gives Berlin's wall clock for an instant,
// and a Berlin wall-clock time is turned into an instant by trying the offsets in force around it.

export const ZONE = 'Europe/Berlin';

const HOUR = 3_600_000;

const partsFormat = new Intl.DateTimeFormat('en-GB', {
  timeZone: ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hourCycle: 'h23',
});

const monthNames = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const weekdayFormat = new Intl.DateTimeFormat('en-GB', { timeZone: ZONE, weekday: 'short' });

/** Berlin's wall clock at an instant: { year, month, day, hour, minute, second } (month 1-12). */
export function berlinParts(instant) {
  const date = toDate(instant);
  const parts = {};
  for (const { type, value } of partsFormat.formatToParts(date)) {
    if (type !== 'literal') {
      parts[type] = Number(value);
    }
  }
  return { year: parts.year, month: parts.month, day: parts.day, hour: parts.hour, minute: parts.minute, second: parts.second };
}

/** Berlin's offset from UTC at an instant, in minutes (60 in winter, 120 in summer). */
export function berlinOffsetMinutes(instant) {
  const date = toDate(instant);
  const p = berlinParts(date);
  const asUtc = Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second);
  return Math.round((asUtc - Math.floor(date.getTime() / 1000) * 1000) / 60_000);
}

/**
 * The instant of a Berlin wall-clock time. A time the spring change skips moves forward by the hour skipped;
 * a time the autumn change repeats takes the first (summer-time) instant.
 * @returns {Date}
 */
export function berlinToUtc({ year, month, day, hour = 0, minute = 0 }) {
  const wall = Date.UTC(year, month - 1, day, hour, minute);
  const before = berlinOffsetMinutes(wall - 12 * HOUR);
  const after = berlinOffsetMinutes(wall + 12 * HOUR);
  const valid = [...new Set([before, after])]
    .map((offset) => wall - offset * 60_000)
    .filter((t) => wall - berlinOffsetMinutes(t) * 60_000 === t)
    .sort((a, b) => a - b);
  if (valid.length > 0) {
    return new Date(valid[0]);
  }
  // In the gap: read the time with the offset before the change, which lands one hour later on the new clock.
  return new Date(wall - before * 60_000);
}

/** `YYYY-MM-DD` of the Berlin day an instant falls on. */
export function berlinDate(instant) {
  const p = berlinParts(instant);
  return `${p.year}-${pad(p.month)}-${pad(p.day)}`;
}

/** The calendar date `days` after `YYYY-MM-DD`. */
export function addDays(date, days) {
  const [year, month, day] = splitDate(date);
  const shifted = new Date(Date.UTC(year, month - 1, day + days));
  return `${shifted.getUTCFullYear()}-${pad(shifted.getUTCMonth() + 1)}-${pad(shifted.getUTCDate())}`;
}

/** A Berlin day as the UTC range from its midnight to the next midnight: 23, 24 or 25 hours. */
export function berlinDayRange(date) {
  const [year, month, day] = splitDate(date);
  const [nextYear, nextMonth, nextDay] = splitDate(addDays(date, 1));
  return {
    from: utcIso(berlinToUtc({ year, month, day })),
    to: utcIso(berlinToUtc({ year: nextYear, month: nextMonth, day: nextDay })),
  };
}

/** The value of an `<input type="datetime-local">` (Berlin wall clock) as a UTC ISO string. */
export function berlinLocalToUtcIso(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value ?? '');
  if (!match) {
    return null;
  }
  const [, year, month, day, hour, minute] = match.map(Number);
  return utcIso(berlinToUtc({ year, month, day, hour, minute }));
}

/** An instant as `<input type="datetime-local">` shows it in Berlin. */
export function utcToBerlinLocal(instant) {
  const p = berlinParts(instant);
  return `${p.year}-${pad(p.month)}-${pad(p.day)}T${pad(p.hour)}:${pad(p.minute)}`;
}

/** ISO 8601 in UTC with `Z` and without milliseconds, as the API expects. */
export function utcIso(instant) {
  return toDate(instant).toISOString().replace(/\.\d{3}Z$/, 'Z');
}

/** `07:00` in Berlin. */
export function formatTime(instant) {
  const p = berlinParts(instant);
  return `${pad(p.hour)}:${pad(p.minute)}`;
}

/** `1 July 2026, 07:00` in Berlin. */
export function formatDateTime(instant) {
  const p = berlinParts(instant);
  return `${p.day} ${monthNames[p.month - 1]} ${p.year}, ${pad(p.hour)}:${pad(p.minute)}`;
}

/** `Wed 1 July 2026` for a `YYYY-MM-DD` Berlin date. */
export function formatDay(date) {
  const [year, month, day] = splitDate(date);
  const noon = berlinToUtc({ year, month, day, hour: 12 });
  return `${weekdayFormat.format(noon)} ${day} ${monthNames[month - 1]} ${year}`;
}

/**
 * A time as people say it: `Today, 07:00`, `Tomorrow, 00:30`, `Yesterday, 23:10`, otherwise `Thu 12 November, 07:00`
 * (with the year when it is not this year). `midSentence` lowercases the relative day.
 */
export function formatWhen(instant, { now = new Date(), midSentence = false } = {}) {
  const day = berlinDate(instant);
  const today = berlinDate(now);
  const time = formatTime(instant);
  const relative = { [addDays(today, -1)]: 'Yesterday', [today]: 'Today', [addDays(today, 1)]: 'Tomorrow' }[day];
  if (relative) {
    return `${midSentence ? relative.toLowerCase() : relative}, ${time}`;
  }
  const date = formatDay(day);
  return day.slice(0, 4) === today.slice(0, 4) ? `${date.replace(/ \d{4}$/, '')}, ${time}` : `${date}, ${time}`;
}

/** Minutes as `15 min`, `1 h` or `2 h 15 min`. */
export function formatDuration(minutes) {
  const hours = Math.floor(minutes / 60);
  const rest = Math.round(minutes - hours * 60);
  if (hours === 0) {
    return `${rest} min`;
  }
  return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
}

/** Hours between two instants, e.g. 23, 24 or 25 for a Berlin day. */
export function hoursBetween(from, to) {
  return (toDate(to).getTime() - toDate(from).getTime()) / HOUR;
}

function toDate(instant) {
  return instant instanceof Date ? instant : new Date(instant);
}

function splitDate(date) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date);
  if (!match) {
    throw new Error(`Not a YYYY-MM-DD date: ${date}`);
  }
  return [Number(match[1]), Number(match[2]), Number(match[3])];
}

function pad(n) {
  return String(n).padStart(2, '0');
}
