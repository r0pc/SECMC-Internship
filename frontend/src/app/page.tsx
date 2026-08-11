import Link from "next/link";
import { Suspense } from "react";

import { Card, CardBody, CardHeader, PageHeader, Stat } from "@/components/card";
import { KpiTile } from "@/components/kpi-tile";
import { SourceHealthCard } from "@/components/source-health";
import { ApiErrorPanel, SkeletonCard } from "@/components/states";
import { TrendChart } from "@/components/trend-chart";
import { attempt, getDashboardSummary, getKpis, getTrend } from "@/lib/api";
import {
  formatAge,
  formatCount,
  formatReferenceDate,
  formatTimestamp,
  subtractMonths,
  todayPkt,
} from "@/lib/format";
import {
  CPI_SERIES_KEY,
  DASHBOARD_KPI_KEYS,
  DEFAULT_RANGE_MONTHS,
  SOFR_PERCENTILE_KEYS,
  SOFR_SERIES_KEY,
  groupByUnit,
} from "@/lib/series";

/**
 * The landing dashboard (FR-10).
 *
 * Each panel fetches its own data inside a Suspense boundary, so a slow endpoint delays that
 * panel rather than the page, and a failed one is reported in place — an operations dashboard
 * that goes blank because one of four calls failed has told the reader nothing.
 */
export default function DashboardPage() {
  return (
    <div className="space-y-8">
      <PageHeader
        title="Dashboard"
        description={
          <>
            Consumer Price Index from the U.S. Bureau of Labor Statistics and the
            Secured Overnight Financing Rate from the Federal Reserve Bank of New
            York, collected hourly and stored exactly as published. Units differ
            between series and are never converted, so each chart carries one unit.
          </>
        }
      />

      <Suspense fallback={<KpiRowFallback />}>
        <KpiRow />
      </Suspense>

      <Suspense fallback={<SkeletonCard rows={10} />}>
        <TrendPanels />
      </Suspense>

      <Suspense fallback={<SkeletonCard rows={6} />}>
        <CoveragePanel />
      </Suspense>
    </div>
  );
}

// ---------------------------------------------------------------------------

async function KpiRow() {
  const result = await attempt(getKpis(DASHBOARD_KPI_KEYS));

  if (!result.ok) {
    return <ApiErrorPanel error={result.error} label="Headline figures" />;
  }

  return (
    <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
      {result.data.map((kpi) => (
        <KpiTile key={kpi.seriesKey} kpi={kpi} />
      ))}
    </div>
  );
}

function KpiRowFallback() {
  return (
    <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
      <SkeletonCard rows={4} />
      <SkeletonCard rows={4} />
      <SkeletonCard rows={4} />
    </div>
  );
}

// ---------------------------------------------------------------------------

/**
 * One request for every chartable line, then one chart per unit.
 *
 * SOFR's rate and its four percentiles share an axis because they share a unit, which is what
 * makes the distribution readable against the rate. CPI is an index and gets its own.
 */
async function TrendPanels() {
  const to = todayPkt();
  const from = subtractMonths(to, DEFAULT_RANGE_MONTHS);

  const result = await attempt(
    getTrend({
      seriesKeys: [CPI_SERIES_KEY, SOFR_SERIES_KEY, ...SOFR_PERCENTILE_KEYS],
      from,
      to,
    }),
  );

  if (!result.ok) {
    return <ApiErrorPanel error={result.error} label="Trend charts" />;
  }

  const groups = groupByUnit(result.data);

  if (groups.length === 0) {
    return null;
  }

  const range = `${formatReferenceDate(from)} to ${formatReferenceDate(to)}`;

  return (
    <div className="grid gap-6 xl:grid-cols-2">
      {groups.map((group) => {
        const title =
          group.series.length === 1 ? group.series[0].title : group.unit;

        return (
          <Card key={group.unit}>
            <CardHeader
              title={title}
              hint={`${range} · ${group.unit}`}
              action={
                <Link
                  href={`/series/${encodeURIComponent(group.series[0].seriesKey)}`}
                  className="text-xs font-medium text-zinc-600 hover:underline dark:text-zinc-400"
                >
                  Open series →
                </Link>
              }
            />
            <CardBody>
              <TrendChart series={group.series} unit={group.unit} />
            </CardBody>
          </Card>
        );
      })}
    </div>
  );
}

// ---------------------------------------------------------------------------

async function CoveragePanel() {
  const result = await attempt(getDashboardSummary());

  if (!result.ok) {
    return <ApiErrorPanel error={result.error} label="Coverage and health" />;
  }

  const summary = result.data;
  // Pinned once so every "x ago" on this render measures from the same instant.
  const now = new Date();

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader
          title="Stored history"
          hint="Row counts are per dataset. A CPI row is a month and a SOFR row is a business day, so they are not comparable and are never summed."
        />
        <CardBody>
          <dl className="grid grid-cols-2 gap-6 lg:grid-cols-5">
            <Stat
              label="CPI observations"
              value={formatCount(summary.cpiObservationCount)}
              hint={span(summary.earliestCpiMonth, summary.latestCpiMonth, true)}
            />
            <Stat
              label="SOFR observations"
              value={formatCount(summary.sofrObservationCount)}
              hint={span(summary.earliestSofrDate, summary.latestSofrDate, false)}
            />
            <Stat label="Sources" value={formatCount(summary.sourceCount)} />
            <Stat
              label="Chartable series"
              value={formatCount(summary.seriesCount)}
            />
            <Stat
              label="Last collection"
              value={formatAge(summary.lastCollectionAtPkt, now)}
              hint={formatTimestamp(summary.lastCollectionAtPkt)}
            />
          </dl>
        </CardBody>
      </Card>

      <section className="space-y-4">
        <div className="flex items-end justify-between gap-4">
          <h2 className="text-sm font-semibold tracking-tight text-zinc-900 dark:text-zinc-100">
            Collection health
          </h2>
          <Link
            href="/collection"
            className="text-xs font-medium text-zinc-600 hover:underline dark:text-zinc-400"
          >
            Collection log →
          </Link>
        </div>

        <div className="grid gap-4 lg:grid-cols-2">
          {summary.sources.map((health) => (
            <SourceHealthCard
              key={health.dataSourceId}
              health={health}
              now={now}
            />
          ))}
        </div>
      </section>
    </div>
  );
}

/** The span of stored history, at the granularity the dataset actually has. */
function span(
  earliest: string | null,
  latest: string | null,
  monthly: boolean,
): string {
  if (!earliest || !latest) {
    return "no history stored";
  }

  const frequency = monthly ? "Monthly" : undefined;

  return `${formatReferenceDate(earliest, frequency)} – ${formatReferenceDate(
    latest,
    frequency,
  )}`;
}
