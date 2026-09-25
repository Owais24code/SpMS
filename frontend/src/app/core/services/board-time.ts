import type { AvailabilityDto } from '../models/api';

/**
 * The one conversion between the server's time model and the board's.
 *
 * The API speaks instants and durations. The board is positioned in
 * percentages across a business day. Those are two different models, and the
 * arithmetic that joins them was previously duplicated in the store, in the
 * schedule component and in the seed data — each with `9 * 60` and `8 * 60`
 * written out, so a property that does not open at 09:00 UTC rendered every
 * appointment in the wrong place.
 *
 * The business day is DERIVED, never assumed: /availability publishes the
 * property's IANA zone alongside the day's slots, and the first and last slot
 * plus the service duration give the open and close instants. Only when that
 * is unavailable does `fallbackDay` build a window from a stated zone, and it
 * says so by construction rather than pretending to know.
 */

export interface BusinessDay {
  /** IANA id as the server published it. */
  readonly timeZone: string;
  /** `yyyy-MM-dd` in the property's zone — what /appointments?date= wants. */
  readonly date: string;
  readonly openUtcMs: number;
  readonly closeUtcMs: number;
  /** False when the window was assumed rather than published. */
  readonly derived: boolean;
}

export const MS_PER_MINUTE = 60_000;

/** Only used when nothing better is available. See PropertyDirectory. */
const FALLBACK_OPEN_MINUTE = 9 * 60;
const FALLBACK_CLOSE_MINUTE = 17 * 60;

/* ------------------------------------------------------------------ */
/* Building a business day                                             */
/* ------------------------------------------------------------------ */

/**
 * The published day.
 *
 * The last slot is the last START that fits the whole treatment before close,
 * so close is that start plus the duration. Taking the last start as close
 * would shorten the board by one service length and push every afternoon
 * appointment off the right-hand edge.
 */
export const dayFromAvailability = (av: AvailabilityDto): BusinessDay => {
  const slots = av.slots;
  if (slots.length === 0) return fallbackDay(av.date, av.timeZone);

  const open = Date.parse(slots[0].startUtc);
  const lastStart = Date.parse(slots[slots.length - 1].startUtc);
  if (!Number.isFinite(open) || !Number.isFinite(lastStart)) {
    return fallbackDay(av.date, av.timeZone);
  }

  return {
    timeZone: av.timeZone,
    date: av.date,
    openUtcMs: open,
    closeUtcMs: lastStart + av.durationMinutes * MS_PER_MINUTE,
    derived: true,
  };
};

/**
 * An assumed 09:00–17:00 in the named zone.
 *
 * Still resolved THROUGH the zone rather than in UTC: an assumed local window
 * is wrong by a known amount, while an assumed UTC window is wrong by the
 * property's offset as well, which is the defect this module exists to remove.
 */
export const fallbackDay = (
  date: string,
  timeZone: string,
  openMinute = FALLBACK_OPEN_MINUTE,
  closeMinute = FALLBACK_CLOSE_MINUTE,
): BusinessDay => ({
  timeZone,
  date,
  openUtcMs: localMinutesToUtcMs(date, timeZone, openMinute),
  closeUtcMs: localMinutesToUtcMs(date, timeZone, closeMinute),
  derived: false,
});

/**
 * Widens a day so an appointment outside opening hours is still visible.
 *
 * An overnight or early booking positioned at a negative percentage is drawn
 * off the board, which reads as "the appointment is gone" — the one thing a
 * board must never do.
 */
export const spanning = (day: BusinessDay, instants: readonly string[]): BusinessDay => {
  let open = day.openUtcMs;
  let close = day.closeUtcMs;

  for (const iso of instants) {
    const ms = Date.parse(iso);
    if (!Number.isFinite(ms)) continue;
    if (ms < open) open = floorToHour(ms);
    if (ms > close) close = ceilToHour(ms);
  }

  return open === day.openUtcMs && close === day.closeUtcMs
    ? day
    : { ...day, openUtcMs: open, closeUtcMs: close };
};

/* ------------------------------------------------------------------ */
/* Instants ⇄ percentages                                              */
/* ------------------------------------------------------------------ */

const spanMs = (day: BusinessDay): number => Math.max(MS_PER_MINUTE, day.closeUtcMs - day.openUtcMs);

/** Clamped, because a slot drawn at -4% is a slot the operator cannot see. */
export const pctAt = (day: BusinessDay, instantIso: string): number => {
  const ms = Date.parse(instantIso);
  if (!Number.isFinite(ms)) return 0;
  return clamp(((ms - day.openUtcMs) / spanMs(day)) * 100);
};

export const pctForMinutes = (day: BusinessDay, minutes: number): number =>
  clamp((minutes * MS_PER_MINUTE / spanMs(day)) * 100);

/** The inverse of pctAt. Returned as an ISO instant, which is what the API wants. */
export const instantAtPct = (day: BusinessDay, pct: number): string =>
  new Date(day.openUtcMs + (clamp(pct) / 100) * spanMs(day)).toISOString();

/**
 * Snaps to a whole number of minutes.
 *
 * A percentage nudge produces instants like 13:07:12.480, and the server's
 * half-hourly availability grid and turnover rules are stated in minutes. An
 * unsnapped proposal preflights against a slot that does not exist.
 */
export const snapToMinutes = (instantIso: string, minutes = 5): string => {
  const ms = Date.parse(instantIso);
  if (!Number.isFinite(ms)) return instantIso;
  const step = minutes * MS_PER_MINUTE;
  return new Date(Math.round(ms / step) * step).toISOString();
};

/* ------------------------------------------------------------------ */
/* Property-local rendering                                            */
/* ------------------------------------------------------------------ */

/**
 * `1:30pm` in the property's zone.
 *
 * The property's, never the viewer's: a scheduler in one city routinely works
 * another, and a board that silently renders in the browser's zone is off by
 * hours while looking entirely plausible.
 */
export const clockLabel = (day: BusinessDay, instantIso: string): string => {
  const ms = Date.parse(instantIso);
  if (!Number.isFinite(ms)) return '—';
  return clockLabelFromMs(day.timeZone, ms);
};

export const clockLabelAtPct = (day: BusinessDay, pct: number): string =>
  clockLabel(day, instantAtPct(day, pct));

/**
 * The board's hour scale.
 *
 * Stepped hourly from the OPEN instant rather than from the next UTC hour: a
 * property in a half-hour zone opens at 09:00 local, and anchoring the scale
 * to UTC hours labels that column 08:30.
 */
export const hourTicks = (day: BusinessDay): readonly string[] => {
  const ticks: string[] = [];
  for (let ms = day.openUtcMs; ms <= day.closeUtcMs && ticks.length <= 24; ms += 3_600_000) {
    ticks.push(hourTick(day.timeZone, ms));
  }
  return ticks;
};

/** Today in the property's zone. The browser's date can be a day out. */
export const propertyToday = (timeZone: string, nowMs = Date.now()): string =>
  isoDate(parts(timeZone, nowMs));

/**
 * Today in the BROWSER's zone.
 *
 * Only for the first render, before any response has said which zone the
 * property is in. Every later date comes from propertyToday.
 */
export const browserToday = (nowMs = Date.now()): string => {
  const d = new Date(nowMs);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

/** Adds whole days in the property's zone, so a DST day is still one day. */
export const shiftDate = (date: string, byDays: number): string => {
  const [y, m, d] = date.split('-').map(Number);
  const at = new Date(Date.UTC(y, (m ?? 1) - 1, d ?? 1));
  at.setUTCDate(at.getUTCDate() + byDays);
  return at.toISOString().slice(0, 10);
};

/* ------------------------------------------------------------------ */
/* Zone arithmetic, without a date library                             */
/* ------------------------------------------------------------------ */

interface ZonedParts {
  readonly year: number;
  readonly month: number;
  readonly day: number;
  readonly hour: number;
  readonly minute: number;
}

const formatters = new Map<string, Intl.DateTimeFormat>();

/**
 * Formatters are memoized: constructing one is expensive, and the board asks
 * for a label per slot per render.
 */
const formatter = (timeZone: string): Intl.DateTimeFormat => {
  const cached = formatters.get(timeZone);
  if (cached !== undefined) return cached;

  let made: Intl.DateTimeFormat;
  try {
    made = new Intl.DateTimeFormat('en-GB', {
      timeZone,
      hourCycle: 'h23',
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit',
    });
  } catch {
    // A misconfigured property must not take the board down; UTC is at least
    // a zone that exists, and the server has the same fallback.
    made = new Intl.DateTimeFormat('en-GB', {
      timeZone: 'UTC',
      hourCycle: 'h23',
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit',
    });
  }
  formatters.set(timeZone, made);
  return made;
};

const parts = (timeZone: string, ms: number): ZonedParts => {
  const found: Record<string, string> = {};
  for (const p of formatter(timeZone).formatToParts(new Date(ms))) {
    if (p.type !== 'literal') found[p.type] = p.value;
  }
  return {
    year: Number(found['year'] ?? '1970'),
    month: Number(found['month'] ?? '01'),
    day: Number(found['day'] ?? '01'),
    // h23 yields 24 for midnight in some engines.
    hour: Number(found['hour'] ?? '0') % 24,
    minute: Number(found['minute'] ?? '0'),
  };
};

const isoDate = (p: ZonedParts): string =>
  `${p.year}-${String(p.month).padStart(2, '0')}-${String(p.day).padStart(2, '0')}`;

/** The zone's offset from UTC at a given instant, in milliseconds. */
const offsetMsAt = (timeZone: string, ms: number): number => {
  const p = parts(timeZone, ms);
  const asIfUtc = Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute);
  // Seconds are dropped by the formatter, so compare on whole minutes.
  return asIfUtc - Math.floor(ms / MS_PER_MINUTE) * MS_PER_MINUTE;
};

/**
 * A local wall-clock minute in a zone, as a UTC instant.
 *
 * Applied twice because the offset depends on the instant we are solving for:
 * the first pass uses the offset at the wrong instant, which is off by an hour
 * across a DST boundary. The second pass uses the offset at the answer.
 */
const localMinutesToUtcMs = (date: string, timeZone: string, minute: number): number => {
  const [y, m, d] = date.split('-').map(Number);
  const wall = Date.UTC(y, (m ?? 1) - 1, d ?? 1) + minute * MS_PER_MINUTE;
  let guess = wall - offsetMsAt(timeZone, wall);
  guess = wall - offsetMsAt(timeZone, guess);
  return guess;
};

const clockLabelFromMs = (timeZone: string, ms: number): string => {
  const p = parts(timeZone, ms);
  const h12 = p.hour % 12 === 0 ? 12 : p.hour % 12;
  return `${h12}:${String(p.minute).padStart(2, '0')}${p.hour < 12 ? 'am' : 'pm'}`;
};

const hourTick = (timeZone: string, ms: number): string => {
  const p = parts(timeZone, ms);
  const h12 = p.hour % 12 === 0 ? 12 : p.hour % 12;
  return `${h12}${p.hour < 12 ? 'a' : 'p'}`;
};

const floorToHour = (ms: number): number => Math.floor(ms / 3_600_000) * 3_600_000;
const ceilToHour = (ms: number): number => Math.ceil(ms / 3_600_000) * 3_600_000;
const clamp = (pct: number): number => Math.max(0, Math.min(100, pct));
