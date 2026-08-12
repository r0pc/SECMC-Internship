import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { cache } from "react";

import { Badge } from "@/components/badges";
import { Card, CardBody, CardHeader, PageHeader } from "@/components/card";
import { FilterLinks } from "@/components/filter-links";
import { KpiTile } from "@/components/kpi-tile";
import { Pagination } from "@/components/pagination";
import { ApiErrorPanel, EmptyState } from "@/components/states";
import { Table, Td, Th, Tr } from "@/components/table";
import { TrendChart } from "@/components/trend-chart";
import {
  ApiError,
  attempt,
  getKpis,
  getObservations,
  getSeries,
  getTrend,
} from "@/lib/api";
import {
  formatReferenceDate,
  formatTimestamp,
  formatValue,
  humanizeEnum,
  subtractMonths,
  todayPkt,
} from "@/lib/format";
import { requireSession } from "@/lib/session";
import { DEFAULT_RANGE_MONTHS } from "@/lib/series";
import { type SearchParams, readEnum, readFlag, readPage } from "@/lib/url";
import type {
  PeriodType,
  SeriesDto,
  SeriesFrequency,
  SortDirection,
  TrendGranularity,
} from "@/types/api";

/**
 * The display granularity of one row.
 *
 * A CPI row carries its own period length, and it is not always the series' frequency — the
 * annual and semiannual averages arrive alongside the monthly figures. SOFR rows carry none,
 * because every row there is one business day.
 */
function rowFrequency(
  periodType: PeriodType | null,
  seriesFrequency: SeriesFrequency,
): SeriesFrequency {
  switch (periodType) {
    case "Month":
      return "Monthly";
    case "Semiannual":
      return "Semiannual";
    case "Annual":
      return "Annual";
    default:
      return seriesFrequency;
  }
}

/**
 * One series in full (FR-10, FR-11): its headline numbers, its shape over time, and the rows
 * behind both.
 *
 * The controls write to the query string rather than to component state, so any view of this page
 * is a link — which matters when the interesting thing about a series is a revision someone needs
 * to point a colleague at.
 */

const OBSERVATION_PAGE_SIZE = 50;

/** CPI's first published month. Wide enough to mean "everything", narrow enough to be a real date. */
const EARLIEST_POSSIBLE = "1913-01-01";

const RANGES = ["3", "6", "12", "24", "60", "max"] as const;
const GRANULARITIES: readonly TrendGranularity[] = [
  "Auto",
  "Point",
  "Month",
  "Quarter",
  "Year",
];
const PERIOD_TYPES: readonly PeriodType[] = ["Month", "Semiannual", "Annual"];
const SORTS: readonly SortDirection[] = ["Descending", "Ascending"];

/**
 * Memoised for the request, so `generateMetadata` and the page itself share one call rather than
 * asking the API the same question twice per page view.
 */
const loadSeries = cache(async (seriesKey: string): Promise<SeriesDto> => {
  try {
    return await getSeries(seriesKey);
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      notFound();
    }

    throw error;
  }
});

export async function generateMetadata({
  params,
}: {
  params: Promise<{ seriesKey: string }>;
}): Promise<Metadata> {
  const { seriesKey } = await params;
  const series = await loadSeries(seriesKey);

  return {
    title: series.title,
    description: `${series.title}. Unit: ${series.unit}. Published by ${series.sourceCode}.`,
  };
}

export default async function SeriesDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ seriesKey: string }>;
  searchParams: Promise<SearchParams>;
}) {
  await requireSession();

  const [{ seriesKey }, query] = await Promise.all([params, searchParams]);
  const series = await loadSeries(seriesKey);

  const pathname = `/series/${encodeURIComponent(series.seriesKey)}`;

  const range = readEnum(query, "range", RANGES) ?? String(DEFAULT_RANGE_MONTHS);
  const granularity = readEnum(query, "granularity", GRANULARITIES);
  const sort = readEnum(query, "sort", SORTS) ?? "Descending";
  const includeRevisions = readFlag(query, "revisions");
  const page = readPage(query);

  // CPI publishes annual and semiannual averages in the same response as its monthly figures.
  // The API defaults to monthly so those cannot land on a monthly chart as a thirteenth month;
  // the filter is how a reader asks for them deliberately. SOFR has no such column.
  const isCpi = series.dataset === "Cpi";
  const periodType = isCpi
    ? (readEnum(query, "periodType", PERIOD_TYPES) ?? "Month")
    : undefined;

  const to = todayPkt();
  const from = range === "max" ? EARLIEST_POSSIBLE : subtractMonths(to, Number(range));

  const [kpiResult, trendResult, observationsResult] = await Promise.all([
    attempt(getKpis([series.seriesKey])),
    attempt(
      getTrend({ seriesKeys: [series.seriesKey], from, to, granularity }),
    ),
    attempt(
      getObservations(series.seriesKey, {
        from,
        to,
        periodType,
        includeRevisions,
        sort,
        page,
        pageSize: OBSERVATION_PAGE_SIZE,
      }),
    ),
  ]);

  const rangeLabel =
    range === "max"
      ? "all stored history"
      : `${formatReferenceDate(from)} to ${formatReferenceDate(to)}`;

  return (
    <div className="space-y-6">
      <PageHeader
        title={series.title}
        description={
          <>
            <span className="font-mono text-xs">{series.seriesKey}</span> ·{" "}
            {series.unit} · published by {series.sourceCode} as{" "}
            <span className="font-mono text-xs">{series.publisherCode}</span>
          </>
        }
        action={
          <a
            href={series.sourceUrl}
            target="_blank"
            rel="noreferrer"
            className="rounded-md border border-zinc-300 px-3 py-1.5 text-sm font-medium text-zinc-700 hover:bg-zinc-50 dark:border-zinc-700 dark:text-zinc-200 dark:hover:bg-zinc-900"
          >
            Publisher page ↗
          </a>
        }
      />

      <div className="flex flex-wrap items-center gap-2">
        <Badge>{humanizeEnum(series.frequency)}</Badge>
        {series.seasonalAdjustment === "NotApplicable" ? null : (
          <Badge>
            {series.seasonalAdjustment === "SeasonallyAdjusted"
              ? "Seasonally adjusted"
              : "Not seasonally adjusted"}
          </Badge>
        )}
        <Badge>{humanizeEnum(series.dataset)} dataset</Badge>
      </div>

      <div className="grid gap-6 lg:grid-cols-[minmax(0,20rem)_1fr]">
        <div>
          {kpiResult.ok && kpiResult.data.length > 0 ? (
            <KpiTile kpi={kpiResult.data[0]} />
          ) : kpiResult.ok ? (
            <EmptyState title="No headline figures for this series yet" />
          ) : (
            <ApiErrorPanel error={kpiResult.error} label="Headline figures" />
          )}
        </div>

        <Card>
          <CardHeader
            title="Trend"
            hint={`${rangeLabel} · ${series.unit}${
              trendResult.ok && trendResult.data[0]
                ? ` · bucketed by ${humanizeEnum(trendResult.data[0].granularity)}`
                : ""
            }`}
          />
          <CardBody className="space-y-4">
            <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
              <FilterLinks
                label="Range"
                paramKey="range"
                pathname={pathname}
                searchParams={query}
                active={range}
                options={[
                  { label: "3M", value: "3" },
                  { label: "6M", value: "6" },
                  { label: "1Y", value: "12" },
                  { label: "2Y", value: "24" },
                  { label: "5Y", value: "60" },
                  { label: "Max", value: "max" },
                ]}
              />
              <FilterLinks
                label="Buckets"
                paramKey="granularity"
                pathname={pathname}
                searchParams={query}
                active={granularity ?? null}
                options={[
                  { label: "Auto", value: null, title: "Chosen from the series' frequency and the range" },
                  { label: "Point", value: "Point" },
                  { label: "Month", value: "Month" },
                  { label: "Quarter", value: "Quarter" },
                  { label: "Year", value: "Year" },
                ]}
              />
            </div>

            {trendResult.ok ? (
              <TrendChart series={trendResult.data} unit={series.unit} />
            ) : (
              <ApiErrorPanel error={trendResult.error} label="The trend chart" />
            )}
          </CardBody>
        </Card>
      </div>

      <Card>
        <CardHeader
          title="Observations"
          hint={
            <>
              {rangeLabel}. Read-only: both fact tables are append-only and
              written solely by the collector (FR-4), which is what makes the
              historical record trustworthy.
            </>
          }
        />
        <CardBody className="space-y-3 border-b border-zinc-200 dark:border-zinc-800">
          <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
            {isCpi ? (
              <FilterLinks
                label="Period"
                paramKey="periodType"
                pathname={pathname}
                searchParams={query}
                active={periodType ?? null}
                options={[
                  { label: "Monthly", value: "Month" },
                  {
                    label: "Semiannual",
                    value: "Semiannual",
                    title: "BLS S01/S02 — averages of the monthly figures",
                  },
                  {
                    label: "Annual",
                    value: "Annual",
                    title: "BLS M13 — the annual average, not a thirteenth month",
                  },
                ]}
              />
            ) : null}

            <FilterLinks
              label="Vintages"
              paramKey="revisions"
              pathname={pathname}
              searchParams={query}
              active={includeRevisions ? "true" : null}
              options={[
                { label: "Current only", value: null },
                {
                  label: "Include revisions",
                  value: "true",
                  title: "Adds superseded vintages — a period appears once per revision",
                },
              ]}
            />

            <FilterLinks
              label="Order"
              paramKey="sort"
              pathname={pathname}
              searchParams={query}
              active={sort}
              options={[
                { label: "Newest first", value: "Descending" },
                { label: "Oldest first", value: "Ascending" },
              ]}
            />
          </div>
        </CardBody>

        {!observationsResult.ok ? (
          <CardBody>
            <ApiErrorPanel
              error={observationsResult.error}
              label="The observations table"
            />
          </CardBody>
        ) : observationsResult.data.items.length === 0 ? (
          <CardBody>
            <EmptyState
              title="No observations in this range"
              description="Widen the range, or check the collection log to see whether the source has reported yet."
              action={
                <Link
                  href="/collection"
                  className="text-sm font-medium text-zinc-700 underline dark:text-zinc-300"
                >
                  Collection log
                </Link>
              }
            />
          </CardBody>
        ) : (
          <>
            <Table caption={`${series.title} observations`}>
              <thead>
                <tr>
                  <Th>Reference date</Th>
                  {isCpi ? <Th>Period</Th> : null}
                  <Th numeric>Value</Th>
                  <Th numeric>Revision</Th>
                  {includeRevisions ? <Th>Vintage</Th> : null}
                  <Th>Collected</Th>
                  <Th numeric>Run</Th>
                  <Th>Annotation</Th>
                </tr>
              </thead>
              <tbody>
                {observationsResult.data.items.map((observation) => (
                  <Tr key={observation.observationId}>
                    <Td>
                      {formatReferenceDate(
                        observation.referenceDate,
                        // The row's own period wins over the series' headline frequency: BLS
                        // publishes the annual average (M13) in the same monthly response, and
                        // printing it as a month would claim it was one.
                        rowFrequency(observation.periodType, series.frequency),
                      )}
                    </Td>
                    {isCpi ? (
                      <Td>
                        <span className="font-mono text-xs">
                          {observation.periodCode ?? "—"}
                        </span>
                        {observation.periodType ? (
                          <p className="mt-0.5 text-xs text-zinc-500 dark:text-zinc-400">
                            {humanizeEnum(observation.periodType)}
                          </p>
                        ) : null}
                      </Td>
                    ) : null}
                    <Td numeric className="font-medium">
                      {formatValue(observation.value, series.decimalPlaces)}
                    </Td>
                    <Td numeric>{observation.revisionNumber}</Td>
                    {includeRevisions ? (
                      <Td>
                        {observation.isCurrent ? (
                          <Badge tone="positive">Current</Badge>
                        ) : (
                          <Badge
                            tone="neutral"
                            title={`Superseded ${formatTimestamp(
                              observation.supersededAtPkt,
                            )}`}
                          >
                            Superseded
                          </Badge>
                        )}
                      </Td>
                    ) : null}
                    <Td>{formatTimestamp(observation.collectedAtPkt)}</Td>
                    <Td numeric>
                      <Link
                        href={`/collection?runId=${observation.collectionRunId}`}
                        className="font-mono text-xs hover:underline"
                      >
                        {observation.collectionRunId}
                      </Link>
                    </Td>
                    <Td>
                      <span className="font-mono text-xs">
                        {observation.sourceAnnotation ?? "—"}
                      </span>
                    </Td>
                  </Tr>
                ))}
              </tbody>
            </Table>

            <Pagination
              result={observationsResult.data}
              pathname={pathname}
              searchParams={query}
              itemLabel="observations"
            />
          </>
        )}
      </Card>
    </div>
  );
}
