import type { Metadata } from "next";
import Link from "next/link";

import { Badge } from "@/components/badges";
import { Card, PageHeader } from "@/components/card";
import { FilterLinks } from "@/components/filter-links";
import { Pagination } from "@/components/pagination";
import { ApiErrorPanel, EmptyState } from "@/components/states";
import { Table, Td, Th, Tr } from "@/components/table";
import { attempt, getSeriesList } from "@/lib/api";
import {
  formatReferenceDate,
  formatTimestamp,
  formatValue,
  humanizeEnum,
} from "@/lib/format";
import { requireSession } from "@/lib/session";
import { type SearchParams, readEnum, readPage, readParam } from "@/lib/url";
import type { Dataset } from "@/types/api";

export const metadata: Metadata = {
  title: "Series",
  description:
    "Every series the platform can chart, with its unit, frequency and latest stored value.",
};

const DATASETS: readonly Dataset[] = ["Cpi", "Sofr"];

const PATHNAME = "/series";

export default async function SeriesPage({
  searchParams,
}: {
  searchParams: Promise<SearchParams>;
}) {
  await requireSession();

  const params = await searchParams;
  const dataset = readEnum(params, "dataset", DATASETS);
  const page = readPage(params);

  // Set by the link from /sources. Kept out of the visible filters: the source and the dataset
  // are the same distinction here, and two controls for one choice is one too many.
  const dataSourceIdRaw = readParam(params, "dataSourceId");
  const dataSourceId = dataSourceIdRaw
    ? Number.parseInt(dataSourceIdRaw, 10)
    : Number.NaN;

  const result = await attempt(
    getSeriesList({
      dataset,
      dataSourceId: Number.isFinite(dataSourceId) ? dataSourceId : undefined,
      page,
    }),
  );

  return (
    <div className="space-y-6">
      <PageHeader
        title="Series catalogue"
        description={
          <>
            Seven series: the CPI, and SOFR&rsquo;s rate, volume and four
            percentiles. Units are not interchangeable — SOFR volume is in
            billions of dollars while the CPI is an index — and values are shown
            at the precision the publisher released them in.
          </>
        }
      />

      <FilterLinks
        label="Dataset"
        paramKey="dataset"
        pathname={PATHNAME}
        searchParams={params}
        active={dataset ?? null}
        options={[
          { label: "All", value: null },
          { label: "CPI", value: "Cpi" },
          { label: "SOFR", value: "Sofr" },
        ]}
      />

      {!result.ok ? (
        <ApiErrorPanel error={result.error} label="The series catalogue" />
      ) : result.data.items.length === 0 ? (
        <EmptyState
          title="No series match this filter"
          description="Clear the dataset filter to see the whole catalogue."
        />
      ) : (
        <Card>
          <Table caption="Series catalogue">
            <thead>
              <tr>
                <Th>Series</Th>
                <Th>Unit</Th>
                <Th>Frequency</Th>
                <Th>Source</Th>
                <Th numeric>Latest value</Th>
                <Th>As of</Th>
              </tr>
            </thead>
            <tbody>
              {result.data.items.map((series) => (
                <Tr key={series.seriesKey}>
                  <Td>
                    <Link
                      href={`/series/${encodeURIComponent(series.seriesKey)}`}
                      className="font-medium text-zinc-900 hover:underline dark:text-zinc-100"
                    >
                      {series.title}
                    </Link>
                    <p className="mt-0.5 font-mono text-xs text-zinc-500 dark:text-zinc-400">
                      {series.seriesKey}
                    </p>
                  </Td>
                  <Td>{series.unit}</Td>
                  <Td>
                    <div className="flex flex-wrap items-center gap-1.5">
                      <Badge>{humanizeEnum(series.frequency)}</Badge>
                      {/* Comparing an adjusted series against an unadjusted one is a silent
                          analytical error, so the catalogue says which this is. */}
                      {series.seasonalAdjustment === "NotApplicable" ? null : (
                        <Badge tone="neutral">
                          {series.seasonalAdjustment === "SeasonallyAdjusted"
                            ? "Adjusted"
                            : "Not adjusted"}
                        </Badge>
                      )}
                    </div>
                  </Td>
                  <Td>
                    <span className="font-mono text-xs">
                      {series.sourceCode}
                    </span>
                    <p className="mt-0.5 font-mono text-xs text-zinc-500 dark:text-zinc-400">
                      {series.publisherCode}
                    </p>
                  </Td>
                  <Td numeric>
                    {series.latest
                      ? formatValue(series.latest.value, series.decimalPlaces)
                      : "—"}
                  </Td>
                  <Td>
                    {series.latest ? (
                      <>
                        {formatReferenceDate(
                          series.latest.referenceDate,
                          series.frequency,
                        )}
                        <p className="mt-0.5 text-xs text-zinc-500 dark:text-zinc-400">
                          collected {formatTimestamp(series.latest.collectedAtPkt)}
                        </p>
                      </>
                    ) : (
                      "—"
                    )}
                  </Td>
                </Tr>
              ))}
            </tbody>
          </Table>

          <Pagination
            result={result.data}
            pathname={PATHNAME}
            searchParams={params}
            itemLabel="series"
          />
        </Card>
      )}
    </div>
  );
}
