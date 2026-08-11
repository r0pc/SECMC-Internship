/**
 * Display formatting.
 *
 * Two rules run through all of it.
 *
 * Values are rendered at the publisher's own precision (`decimalPlaces` from the catalogue) and
 * never rescaled — CPI is an index to three places, SOFR volume is whole billions of dollars, and
 * rounding either to a house style would misreport a published figure.
 *
 * Timestamps are formatted in UTC, explicitly. Every timestamp the platform stores is UTC, these
 * helpers run on the server and again during hydration, and a browser-local format would both
 * mislabel the reading and produce a hydration mismatch.
 */

import type { IsoDate, IsoUtcTimestamp, SeriesFrequency } from "@/types/api";

/** Fixed locale so the server and the browser agree, whatever the viewer's machine is set to. */
const LOCALE = "en-GB";

const countFormatter = new Intl.NumberFormat(LOCALE, { useGrouping: true });

const monthFormatter = new Intl.DateTimeFormat(LOCALE, {
  month: "long",
  year: "numeric",
  timeZone: "UTC",
});

const dayFormatter = new Intl.DateTimeFormat(LOCALE, {
  day: "numeric",
  month: "short",
  year: "numeric",
  timeZone: "UTC",
});

/** The one clock the platform reports in. Mirrors PakistanTime.IanaId on the backend. */
export const PAKISTAN_TIME_ZONE = "Asia/Karachi";

const timestampFormatter = new Intl.DateTimeFormat(LOCALE, {
  day: "numeric",
  month: "short",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
  // The platform records and reports in Pakistan Standard Time (see PakistanTime in
  // DataIntelligence.Core). Pinned rather than left to the viewer's machine: a timestamp that
  // renders differently in Karachi and on a CI box in UTC is a timestamp nobody can quote.
  timeZone: PAKISTAN_TIME_ZONE,
});

/** Shown wherever a nullable field is genuinely absent, rather than zero. */
export const NOT_AVAILABLE = "—";

function parseIso(value: string): Date | null {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

/** A value at the publisher's precision. */
export function formatValue(
  value: number | null | undefined,
  decimalPlaces: number,
): string {
  if (value === null || value === undefined) {
    return NOT_AVAILABLE;
  }

  return new Intl.NumberFormat(LOCALE, {
    minimumFractionDigits: decimalPlaces,
    maximumFractionDigits: decimalPlaces,
  }).format(value);
}

/**
 * A change, with its sign always shown. A leading `+` is the point: without it, "0.31" and
 * "-0.31" scan as the same kind of thing at a glance.
 */
export function formatChange(
  value: number | null | undefined,
  decimalPlaces: number,
): string {
  if (value === null || value === undefined) {
    return NOT_AVAILABLE;
  }

  return new Intl.NumberFormat(LOCALE, {
    minimumFractionDigits: decimalPlaces,
    maximumFractionDigits: decimalPlaces,
    signDisplay: "exceptZero",
  }).format(value);
}

/** A percentage the API has already expressed as one — 2.83 becomes `+2.83%`. */
export function formatPercentChange(
  value: number | null | undefined,
  decimalPlaces = 2,
): string {
  if (value === null || value === undefined) {
    return NOT_AVAILABLE;
  }

  const formatted = new Intl.NumberFormat(LOCALE, {
    minimumFractionDigits: decimalPlaces,
    maximumFractionDigits: decimalPlaces,
    signDisplay: "exceptZero",
  }).format(value);

  return `${formatted}%`;
}

/** A plain percentage, unsigned — success rates and the like. */
export function formatPercent(
  value: number | null | undefined,
  decimalPlaces = 1,
): string {
  if (value === null || value === undefined) {
    return NOT_AVAILABLE;
  }

  return `${new Intl.NumberFormat(LOCALE, {
    minimumFractionDigits: decimalPlaces,
    maximumFractionDigits: decimalPlaces,
  }).format(value)}%`;
}

/** A row count or similar whole number, grouped. */
export function formatCount(value: number | null | undefined): string {
  return value === null || value === undefined
    ? NOT_AVAILABLE
    : countFormatter.format(value);
}

/**
 * A reference date at the granularity the series actually has.
 *
 * A monthly CPI figure is stored against the first of the month; printing "1 June 2026" would
 * claim a precision the number does not have.
 */
export function formatReferenceDate(
  value: IsoDate | null | undefined,
  frequency?: SeriesFrequency,
): string {
  if (!value) {
    return NOT_AVAILABLE;
  }

  const date = parseIso(value);

  if (!date) {
    return value;
  }

  switch (frequency) {
    case "Monthly":
    case "Semiannual":
      return monthFormatter.format(date);
    case "Annual":
      return String(date.getUTCFullYear());
    default:
      return dayFormatter.format(date);
  }
}

/** A day, always. For trend buckets and table rows where the bucket width is shown separately. */
export function formatDay(value: IsoDate | null | undefined): string {
  if (!value) {
    return NOT_AVAILABLE;
  }

  const date = parseIso(value);
  return date ? dayFormatter.format(date) : value;
}

/** A stored timestamp, labelled UTC because that is what it is. */
export function formatTimestamp(
  value: IsoUtcTimestamp | null | undefined,
): string {
  if (!value) {
    return NOT_AVAILABLE;
  }

  const date = parseIso(value);
  return date ? `${timestampFormatter.format(date)} UTC` : value;
}

/**
 * How long ago something happened, coarsely.
 *
 * Server-rendered only — it reads the clock, so rendering it in a Client Component would
 * disagree with the HTML it hydrates. `now` is a parameter so a caller can pin it.
 */
export function formatAge(
  value: IsoUtcTimestamp | null | undefined,
  now: Date = new Date(),
): string {
  if (!value) {
    return NOT_AVAILABLE;
  }

  const date = parseIso(value);

  if (!date) {
    return NOT_AVAILABLE;
  }

  const minutes = Math.round((now.getTime() - date.getTime()) / 60_000);

  if (minutes < 0) {
    return "just now";
  }

  if (minutes < 1) {
    return "moments ago";
  }

  if (minutes < 60) {
    return `${minutes} min ago`;
  }

  const hours = Math.floor(minutes / 60);

  if (hours < 48) {
    return `${hours} h ago`;
  }

  return `${Math.floor(hours / 24)} d ago`;
}

/** A run duration. Milliseconds below a second, seconds above it — nobody reads "1240 ms" as 1.2s. */
export function formatDuration(milliseconds: number | null | undefined): string {
  if (milliseconds === null || milliseconds === undefined) {
    return NOT_AVAILABLE;
  }

  if (milliseconds < 1000) {
    return `${countFormatter.format(Math.round(milliseconds))} ms`;
  }

  const seconds = milliseconds / 1000;

  if (seconds < 60) {
    return `${seconds.toFixed(seconds < 10 ? 2 : 1)} s`;
  }

  const wholeMinutes = Math.floor(seconds / 60);
  const remainder = Math.round(seconds % 60);

  return `${wholeMinutes} m ${remainder} s`;
}

/**
 * Enum names that carry an acronym, which the generic rule below would flatten into "Rest api"
 * or "Cpi". Listed rather than inferred: there is no way to tell an acronym from a word by its
 * shape, and getting a publisher's own name wrong on screen is not a small error.
 */
const ENUM_LABELS: Record<string, string> = {
  Cpi: "CPI",
  Sofr: "SOFR",
  RestApi: "REST API",
  Html: "HTML",
  Csv: "CSV",
  HttpError: "HTTP error",
};

/** Splits a camel-cased enum name for display: `PartialSuccess` becomes `Partial success`. */
export function humanizeEnum(value: string | null | undefined): string {
  if (!value) {
    return NOT_AVAILABLE;
  }

  const known = ENUM_LABELS[value];

  if (known) {
    return known;
  }

  const words = value.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
  return words.charAt(0).toUpperCase() + words.slice(1).toLowerCase();
}

/**
 * Today in Pakistan Standard Time as a `DateOnly`, for building default ranges.
 *
 * Not `toISOString().slice(0, 10)`, which is today in UTC: for the five hours after midnight PKT
 * that returns yesterday, so a "last 30 days" range built just after midnight would quietly start
 * and end a day early for every user in Pakistan.
 */
export function todayPkt(now: Date = new Date()): IsoDate {
  // en-CA formats as yyyy-MM-dd, which is the shape IsoDate wants.
  return new Intl.DateTimeFormat("en-CA", { timeZone: PAKISTAN_TIME_ZONE }).format(now);
}

/** A `DateOnly` a whole number of months before another. Clamps to the last valid day. */
export function subtractMonths(date: IsoDate, months: number): IsoDate {
  const parsed = parseIso(date) ?? new Date();
  const target = new Date(
    Date.UTC(parsed.getUTCFullYear(), parsed.getUTCMonth() - months, 1),
  );

  const lastDayOfTarget = new Date(
    Date.UTC(target.getUTCFullYear(), target.getUTCMonth() + 1, 0),
  ).getUTCDate();

  target.setUTCDate(Math.min(parsed.getUTCDate(), lastDayOfTarget));

  return target.toISOString().slice(0, 10);
}
