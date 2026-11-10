// The price chart as plain numbers: one bar per 15-minute slot, planned slots marked, hour labels in Berlin time.
// app.mjs turns the model into SVG elements; the same numbers are also shown in a table.

import { ctPerKwh } from './format.mjs';
import { berlinParts, formatTime } from './time.mjs';

const DEFAULT_PADDING = { top: 12, right: 8, bottom: 26, left: 56 };
const tickFormat = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 1, signDisplay: 'negative' });

/**
 * @param {{ start: string, end: string, priceEurPerMwh: number }[]} prices The slots, oldest first.
 * @param {{ planned?: Iterable<string>, width?: number, height?: number, labelEveryHours?: number }} [options]
 *   `planned` holds the start times (ISO) of the plan's slots.
 */
export function priceChart(prices, { planned = [], width = 960, height = 220, labelEveryHours = 3 } = {}) {
  const padding = DEFAULT_PADDING;
  const plannedStarts = new Set([...planned].map((s) => new Date(s).getTime()));
  const values = prices.map((p) => p.priceEurPerMwh);
  const low = Math.min(0, ...values);
  let high = Math.max(0, ...values);
  if (high === low) {
    high = low + 1;
  }

  const plotWidth = width - padding.left - padding.right;
  const plotHeight = height - padding.top - padding.bottom;
  const y = (value) => padding.top + ((high - value) / (high - low)) * plotHeight;
  const zeroY = y(0);
  const slotWidth = prices.length > 0 ? plotWidth / prices.length : 0;
  const gap = slotWidth > 4 ? 1 : 0;

  const bars = prices.map((p, i) => {
    const top = Math.min(y(p.priceEurPerMwh), zeroY);
    const isPlanned = plannedStarts.has(new Date(p.start).getTime());
    return {
      x: round(padding.left + i * slotWidth),
      y: round(top),
      width: round(Math.max(slotWidth - gap, 0.5)),
      height: round(Math.max(Math.abs(y(p.priceEurPerMwh) - zeroY), 1)),
      start: p.start,
      end: p.end,
      price: p.priceEurPerMwh,
      planned: isPlanned,
      negative: p.priceEurPerMwh < 0,
      title: `${formatTime(p.start)}–${formatTime(p.end)} · ${ctPerKwh(p.priceEurPerMwh)}${isPlanned ? ' · planned' : ''}`,
    };
  });

  const hourLabels = [];
  const seen = new Set();
  for (const bar of bars) {
    const { hour, minute, day } = berlinParts(bar.start);
    const key = `${day}-${hour}`;
    if (minute === 0 && hour % labelEveryHours === 0 && !seen.has(key)) {
      seen.add(key);
      hourLabels.push({ x: bar.x, text: formatTime(bar.start) });
    }
  }

  const tickValues = [...new Set([low, 0, high])].sort((a, b) => b - a);
  // Ticks are labelled in ct/kWh, like the bar titles; the bars keep the API's EUR/MWh, which only scales them.
  const yTicks = tickValues.map((value) => ({ y: round(y(value)), text: tickFormat.format(value / 10) }));

  return { width, height, padding, bars, hourLabels, yTicks, zeroY: round(zeroY), unit: 'ct/kWh' };
}

function round(n) {
  return Math.round(n * 100) / 100;
}
