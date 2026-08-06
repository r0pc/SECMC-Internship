import Link from "next/link";

import { Badge, ChangeBadge } from "@/components/badges";
import {
  NOT_AVAILABLE,
  formatChange,
  formatPercentChange,
  formatReferenceDate,
  formatValue,
  humanizeEnum,
} from "@/lib/format";
import type { SeriesKpiDto } from "@/types/api";

/**
 * One series' headline numbers (FR-10).
 *
 * The unit is shown next to the value rather than assumed, and each comparison names the period
 * it is against — "vs previous release" is meaningless without saying which release that was,
 * particularly for CPI, where the previous release can be a month or a revision of one.
 */
export function KpiTile({ kpi }: { kpi: SeriesKpiDto }) {
  const { latest, decimalPlaces } = kpi;

  return (
    <article className="flex flex-col rounded-lg border border-zinc-200 bg-white p-5 dark:border-zinc-800 dark:bg-zinc-950">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <Link
            href={`/series/${encodeURIComponent(kpi.seriesKey)}`}
            className="text-sm font-semibold text-zinc-900 hover:underline dark:text-zinc-100"
          >
            {kpi.title}
          </Link>
          <p className="mt-1 text-xs text-zinc-500 dark:text-zinc-400">
            {kpi.unit}
          </p>
        </div>
        <Badge tone="neutral" title="Publication frequency">
          {humanizeEnum(kpi.frequency)}
        </Badge>
      </div>

      {latest ? (
        <>
          <p className="mt-4 text-3xl font-semibold tabular-nums tracking-tight text-zinc-900 dark:text-zinc-50">
            {formatValue(latest.value, decimalPlaces)}
          </p>
          <p className="mt-1 text-xs text-zinc-500 dark:text-zinc-400">
            {formatReferenceDate(latest.referenceDate, kpi.frequency)}
          </p>

          <dl className="mt-4 space-y-2 border-t border-zinc-100 pt-4 text-sm dark:border-zinc-800">
            <Comparison
              label="vs previous release"
              since={formatReferenceDate(
                kpi.previousReferenceDate,
                kpi.frequency,
              )}
              change={kpi.changeFromPrevious}
              changeFormatted={formatChange(
                kpi.changeFromPrevious,
                decimalPlaces,
              )}
              percent={kpi.percentChangeFromPrevious}
            />
            <Comparison
              label="vs year ago"
              since={formatReferenceDate(
                kpi.yearAgoReferenceDate,
                kpi.frequency,
              )}
              change={kpi.changeFromYearAgo}
              changeFormatted={formatChange(kpi.changeFromYearAgo, decimalPlaces)}
              percent={kpi.percentChangeFromYearAgo}
            />
          </dl>
        </>
      ) : (
        <p className="mt-4 flex-1 text-sm text-zinc-500 dark:text-zinc-400">
          Nothing collected for this series yet.
        </p>
      )}
    </article>
  );
}

/**
 * One comparison line. The absolute change leads and the percentage follows in brackets: a move
 * of 0.31 index points and one of 0.31% are different claims, and the unit-bearing one is the
 * figure the publisher actually released.
 */
function Comparison({
  label,
  since,
  change,
  changeFormatted,
  percent,
}: {
  label: string;
  since: string;
  change: number | null;
  changeFormatted: string;
  percent: number | null;
}) {
  return (
    <div className="flex items-baseline justify-between gap-3">
      <dt className="text-xs text-zinc-500 dark:text-zinc-400">
        {label}
        {since === NOT_AVAILABLE ? null : (
          <span className="ml-1 text-zinc-400 dark:text-zinc-500">
            ({since})
          </span>
        )}
      </dt>
      <dd className="shrink-0 text-right">
        <ChangeBadge value={change} formatted={changeFormatted} />
        {percent === null ? null : (
          <span className="ml-2 text-xs tabular-nums text-zinc-500 dark:text-zinc-400">
            {formatPercentChange(percent)}
          </span>
        )}
      </dd>
    </div>
  );
}
