// Numbers and labels as the dashboard shows them. Amounts come from the API already rounded to the cent;
// they are shown with two decimals and never recomputed.

const eurFormat = new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'EUR', minimumFractionDigits: 2, maximumFractionDigits: 2 });
const oneDecimal = new Intl.NumberFormat('en-GB', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
const twoDecimals = new Intl.NumberFormat('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const upToOneDecimal = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 1 });

/** `€1.20`; a missing value shows a dash. */
export function eur(amount) {
  return isNumber(amount) ? eurFormat.format(amount) : '–';
}

/** Watts as `11.0 kW`. */
export function kw(powerW) {
  return isNumber(powerW) ? `${oneDecimal.format(powerW / 1000)} kW` : '–';
}

/** Watt-hours as `12.35 kWh`. */
export function kwh(energyWh) {
  return isNumber(energyWh) ? `${twoDecimals.format(energyWh / 1000)} kWh` : '–';
}

/** `45%`, or `40.5%`. */
export function percent(value) {
  return isNumber(value) ? `${upToOneDecimal.format(value)}%` : '–';
}

const priceFormat = new Intl.NumberFormat('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2, signDisplay: 'negative' });

/** A day-ahead price, which the API gives in EUR/MWh, as `8.52 ct/kWh` (1 €/MWh is 0.1 ct/kWh). */
export function ctPerKwh(priceEurPerMwh) {
  return isNumber(priceEurPerMwh) ? `${priceFormat.format(priceEurPerMwh / 10)} ct/kWh` : '–';
}

const PROVIDER_NAMES = { ocpp: 'OCPP', carcloud: 'car cloud', heatpumpcloud: 'heat-pump cloud' };

/**
 * Readable names for the owner's devices, keyed by id: the type, numbered when there are several of one type
 * (in the order given), and the facts that tell a device apart. Ids stay out of the name.
 * @returns {Map<string, { name: string, detail: string, choice: string }>}
 */
export function deviceNames(devices) {
  const perType = new Map();
  for (const device of devices) {
    perType.set(device.type, (perType.get(device.type) ?? 0) + 1);
  }
  const counted = new Map();
  const names = new Map();
  for (const device of devices) {
    const n = (counted.get(device.type) ?? 0) + 1;
    counted.set(device.type, n);
    const facts = [];
    if (isNumber(device.maxPowerW)) {
      facts.push(`${kw(device.maxPowerW)} max`);
    }
    if (isNumber(device.batteryCapacityWh)) {
      facts.push(`${upToOneDecimal.format(device.batteryCapacityWh / 1000)} kWh battery`);
    }
    facts.push(`via ${PROVIDER_NAMES[device.providerId] ?? device.providerId}`);
    const name = perType.get(device.type) > 1 ? `${label(device.type)} ${n}` : label(device.type);
    // A choice in a form has little room: the name and the first fact only.
    const choice = facts.length > 1 ? `${name} (${facts[0]})` : name;
    names.set(device.id, { name, detail: facts.join(' · '), choice });
  }
  return names;
}

/** The last four characters of a long id, `…K6A1`, for when nothing better names a thing. */
export function shortId(id) {
  const text = String(id ?? '');
  return text.length > 10 ? `…${text.slice(-4)}` : text;
}

/** A PascalCase enum value as words: `DeadLettered` → `Dead lettered`. */
export function label(value) {
  if (typeof value !== 'string' || value === '') {
    return '–';
  }
  const words = value.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

function isNumber(value) {
  return typeof value === 'number' && Number.isFinite(value);
}
