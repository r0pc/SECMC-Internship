import type { ReactNode } from "react";

import { humanizeEnum } from "@/lib/format";
import type { CollectionRunStatus } from "@/types/api";

type Tone = "neutral" | "positive" | "warning" | "negative" | "info";

const TONE_CLASSES: Record<Tone, string> = {
  neutral:
    "bg-zinc-100 text-zinc-700 ring-zinc-200 dark:bg-zinc-800 dark:text-zinc-300 dark:ring-zinc-700",
  positive:
    "bg-emerald-50 text-emerald-700 ring-emerald-200 dark:bg-emerald-950 dark:text-emerald-300 dark:ring-emerald-900",
  warning:
    "bg-amber-50 text-amber-800 ring-amber-200 dark:bg-amber-950 dark:text-amber-300 dark:ring-amber-900",
  negative:
    "bg-red-50 text-red-700 ring-red-200 dark:bg-red-950 dark:text-red-300 dark:ring-red-900",
  info: "bg-blue-50 text-blue-700 ring-blue-200 dark:bg-blue-950 dark:text-blue-300 dark:ring-blue-900",
};

export function Badge({
  children,
  tone = "neutral",
  title,
}: {
  children: ReactNode;
  tone?: Tone;
  title?: string;
}) {
  return (
    <span
      title={title}
      className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${TONE_CLASSES[tone]}`}
    >
      {children}
    </span>
  );
}

/**
 * Colour is not the only signal here — the status is always spelled out, because a reader who
 * cannot separate the amber from the red still has to be able to tell a partial run from a
 * failed one.
 */
const STATUS_TONES: Record<CollectionRunStatus, Tone> = {
  Succeeded: "positive",
  PartialSuccess: "warning",
  Failed: "negative",
  Running: "info",
  Skipped: "neutral",
};

export function CollectionStatusBadge({
  status,
}: {
  status: CollectionRunStatus | null;
}) {
  if (!status) {
    return <Badge tone="neutral">No runs</Badge>;
  }

  return <Badge tone={STATUS_TONES[status]}>{humanizeEnum(status)}</Badge>;
}

/**
 * A change, coloured by direction and always carrying its sign.
 *
 * Deliberately not labelled good or bad. Whether a rising CPI or a falling SOFR volume is
 * welcome depends on who is reading, and the dashboard has no business deciding.
 */
export function ChangeBadge({
  value,
  formatted,
}: {
  value: number | null | undefined;
  formatted: string;
}) {
  if (value === null || value === undefined) {
    return (
      <span className="text-sm text-zinc-400 dark:text-zinc-500">{formatted}</span>
    );
  }

  const tone =
    value > 0
      ? "text-emerald-700 dark:text-emerald-400"
      : value < 0
        ? "text-red-700 dark:text-red-400"
        : "text-zinc-600 dark:text-zinc-400";

  return (
    <span className={`text-sm font-medium tabular-nums ${tone}`}>
      {formatted}
    </span>
  );
}
