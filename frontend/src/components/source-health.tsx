import { Badge, CollectionStatusBadge } from "@/components/badges";
import {
  NOT_AVAILABLE,
  formatAge,
  formatCount,
  formatPercent,
  formatTimestamp,
  humanizeEnum,
} from "@/lib/format";
import type { SourceHealthDto } from "@/types/api";

/**
 * One source's collection health (NFR Reliability — the ≥99% target).
 *
 * Two numbers say different things and both are shown. The success rate is the window's record;
 * consecutive failures is whether collection is broken *now*. A source can sit at 99% and still
 * have been dead since yesterday, which is the case an operations panel exists to catch.
 */
export function SourceHealthCard({
  health,
  now,
}: {
  health: SourceHealthDto;
  /** Pinned by the caller so every card on a page ages against the same instant. */
  now: Date;
}) {
  const broken = health.consecutiveFailures > 0;

  return (
    <article
      className={`rounded-lg border p-5 ${
        broken
          ? "border-red-300 bg-red-50/60 dark:border-red-900 dark:bg-red-950/30"
          : "border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950"
      }`}
    >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-zinc-900 dark:text-zinc-100">
            {health.name}
          </h3>
          <p className="mt-0.5 font-mono text-xs text-zinc-500 dark:text-zinc-400">
            {health.sourceCode}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {health.isEnabled ? null : <Badge tone="neutral">Disabled</Badge>}
          <CollectionStatusBadge status={health.lastRunStatus} />
        </div>
      </div>

      <dl className="mt-4 grid grid-cols-2 gap-x-4 gap-y-3 text-sm sm:grid-cols-4">
        <div>
          <dt className="text-xs text-zinc-500 dark:text-zinc-400">
            Success rate
          </dt>
          <dd className="mt-0.5 font-semibold tabular-nums text-zinc-900 dark:text-zinc-100">
            {/* Null, not 100: a window with no runs at all is a different state to a clean one. */}
            {health.successRatePercent === null
              ? NOT_AVAILABLE
              : formatPercent(health.successRatePercent)}
          </dd>
          <dd className="text-xs text-zinc-500 dark:text-zinc-400">
            last {health.windowDays} days
          </dd>
        </div>

        <div>
          <dt className="text-xs text-zinc-500 dark:text-zinc-400">Runs</dt>
          <dd className="mt-0.5 font-semibold tabular-nums text-zinc-900 dark:text-zinc-100">
            {formatCount(health.totalRuns)}
          </dd>
          <dd className="text-xs text-zinc-500 dark:text-zinc-400">
            {formatCount(health.failedRuns)} failed,{" "}
            {formatCount(health.partialRuns)} partial
          </dd>
        </div>

        <div>
          <dt className="text-xs text-zinc-500 dark:text-zinc-400">Last run</dt>
          <dd className="mt-0.5 font-semibold text-zinc-900 dark:text-zinc-100">
            {formatAge(health.lastRunAtUtc, now)}
          </dd>
          <dd className="text-xs text-zinc-500 dark:text-zinc-400">
            {formatTimestamp(health.lastRunAtUtc)}
          </dd>
        </div>

        <div>
          <dt className="text-xs text-zinc-500 dark:text-zinc-400">
            Last success
          </dt>
          <dd className="mt-0.5 font-semibold text-zinc-900 dark:text-zinc-100">
            {formatAge(health.lastSuccessAtUtc, now)}
          </dd>
          <dd className="text-xs text-zinc-500 dark:text-zinc-400">
            {formatTimestamp(health.lastSuccessAtUtc)}
          </dd>
        </div>
      </dl>

      {broken ? (
        <p className="mt-4 rounded-md bg-red-100 px-3 py-2 text-sm text-red-800 dark:bg-red-950 dark:text-red-300">
          <strong className="font-semibold">
            {formatCount(health.consecutiveFailures)} consecutive failure
            {health.consecutiveFailures === 1 ? "" : "s"}
          </strong>
          {health.lastFailureCategory ? (
            <> — {humanizeEnum(health.lastFailureCategory)}</>
          ) : null}
          {health.lastErrorMessage ? (
            <span className="mt-1 block font-mono text-xs leading-5 break-words">
              {health.lastErrorMessage}
            </span>
          ) : null}
        </p>
      ) : null}
    </article>
  );
}
