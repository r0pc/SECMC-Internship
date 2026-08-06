import type { Metadata } from "next";
import { Suspense } from "react";

import { Badge, CollectionStatusBadge } from "@/components/badges";
import { Card, CardBody, CardHeader, PageHeader, Stat } from "@/components/card";
import { FilterLinks, type FilterOption } from "@/components/filter-links";
import { Pagination } from "@/components/pagination";
import { SourceHealthCard } from "@/components/source-health";
import { ApiErrorPanel, EmptyState, SkeletonCard } from "@/components/states";
import { Table, Td, Th, Tr } from "@/components/table";
import {
  attempt,
  getCollectionHealth,
  getCollectionRun,
  getCollectionRuns,
} from "@/lib/api";
import {
  formatCount,
  formatDuration,
  formatTimestamp,
  humanizeEnum,
} from "@/lib/format";
import {
  type SearchParams,
  readEnum,
  readFlag,
  readPage,
  readParam,
} from "@/lib/url";
import type { CollectionRunStatus } from "@/types/api";

/**
 * The collection log and its health metrics (FR-2, NFR Reliability).
 *
 * Every cycle is recorded, including the ones that fail. That is the whole point of the panel: a
 * dashboard whose numbers stop updating looks exactly like one whose numbers have not changed,
 * and this is where the difference is visible.
 */

export const metadata: Metadata = {
  title: "Collection",
  description:
    "Per-source collection health and the log of every collection attempt, successful or not.",
};

const PATHNAME = "/collection";
const RUNS_PAGE_SIZE = 50;

const STATUSES: readonly CollectionRunStatus[] = [
  "Succeeded",
  "PartialSuccess",
  "Failed",
  "Running",
  "Skipped",
];

export default async function CollectionPage({
  searchParams,
}: {
  searchParams: Promise<SearchParams>;
}) {
  const params = await searchParams;

  return (
    <div className="space-y-6">
      <PageHeader
        title="Collection"
        description={
          <>
            Hourly collection per source, with failures categorised (FR-2).
            Success rate counts succeeded and partial runs over completed
            attempts — runs still in flight and skipped cycles are excluded, so
            the figure means what the ≥99% target measures.
          </>
        }
      />

      <Suspense fallback={<SkeletonCard rows={5} />}>
        <HealthSection />
      </Suspense>

      <Suspense fallback={<SkeletonCard rows={4} />}>
        <FocusedRun params={params} />
      </Suspense>

      <Suspense fallback={<SkeletonCard rows={12} />}>
        <RunsSection params={params} />
      </Suspense>
    </div>
  );
}

// ---------------------------------------------------------------------------

async function HealthSection() {
  const result = await attempt(getCollectionHealth());

  if (!result.ok) {
    return <ApiErrorPanel error={result.error} label="Collection health" />;
  }

  const now = new Date();

  return (
    <div className="grid gap-4 lg:grid-cols-2">
      {result.data.map((health) => (
        <SourceHealthCard key={health.dataSourceId} health={health} now={now} />
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------

/**
 * A single run, when one was linked to.
 *
 * An observation row on a series page names the run that wrote it; following that link and
 * landing on page 1 of the log would leave the reader to hunt for it.
 */
async function FocusedRun({ params }: { params: SearchParams }) {
  const raw = readParam(params, "runId");

  if (!raw) {
    return null;
  }

  const runId = Number.parseInt(raw, 10);

  if (!Number.isFinite(runId)) {
    return null;
  }

  const result = await attempt(getCollectionRun(runId));

  if (!result.ok) {
    return <ApiErrorPanel error={result.error} label={`Run ${raw}`} />;
  }

  const run = result.data;

  return (
    <Card className="border-blue-300 dark:border-blue-900">
      <CardHeader
        title={`Run ${formatCount(run.collectionRunId)} — ${run.sourceCode}`}
        hint={`${humanizeEnum(run.triggerType)}, attempt ${run.attempt}, scheduled for ${formatTimestamp(
          run.scheduledForUtc,
        )}`}
        action={<CollectionStatusBadge status={run.status} />}
      />
      <CardBody>
        <dl className="grid grid-cols-2 gap-6 sm:grid-cols-3 lg:grid-cols-6">
          <Stat label="Started" value={formatTimestamp(run.startedAtUtc)} />
          <Stat label="Duration" value={formatDuration(run.durationMs)} />
          <Stat label="Fetched" value={formatCount(run.observationsFetched)} />
          <Stat label="Inserted" value={formatCount(run.observationsInserted)} />
          {/* Counted apart from inserts: a revision means a published figure moved. */}
          <Stat label="Revised" value={formatCount(run.observationsRevised)} />
          <Stat label="Rejected" value={formatCount(run.observationsRejected)} />
        </dl>

        {run.failureCategory || run.errorMessage ? (
          <div className="mt-5 rounded-md bg-red-50 px-4 py-3 text-sm text-red-800 dark:bg-red-950/50 dark:text-red-300">
            {run.failureCategory ? (
              <p className="font-semibold">
                {humanizeEnum(run.failureCategory)}
                {run.httpStatusCode ? ` — HTTP ${run.httpStatusCode}` : ""}
              </p>
            ) : null}
            {run.errorMessage ? (
              <p className="mt-1 font-mono text-xs leading-5 break-words">
                {run.errorMessage}
              </p>
            ) : null}
          </div>
        ) : null}
      </CardBody>
    </Card>
  );
}

// ---------------------------------------------------------------------------

async function RunsSection({ params }: { params: SearchParams }) {
  const status = readEnum(params, "status", STATUSES);
  const failuresOnly = readFlag(params, "failuresOnly");
  const dataSourceIdRaw = readParam(params, "dataSourceId");
  const dataSourceId = dataSourceIdRaw
    ? Number.parseInt(dataSourceIdRaw, 10)
    : undefined;
  const page = readPage(params);

  const [runsResult, healthResult] = await Promise.all([
    attempt(
      getCollectionRuns({
        status,
        failuresOnly,
        dataSourceId:
          dataSourceId !== undefined && Number.isFinite(dataSourceId)
            ? dataSourceId
            : undefined,
        page,
        pageSize: RUNS_PAGE_SIZE,
      }),
    ),
    // Only for the filter's labels. A failure here costs the source names, not the log.
    attempt(getCollectionHealth()),
  ]);

  const sourceOptions: FilterOption[] = [
    { label: "All sources", value: null },
    ...(healthResult.ok
      ? healthResult.data.map((health) => ({
          label: health.sourceCode,
          value: String(health.dataSourceId),
          title: health.name,
        }))
      : []),
  ];

  const activeStatus = failuresOnly ? "failures" : (status ?? null);

  return (
    <Card>
      <CardHeader
        title="Collection log"
        hint="Newest first. Every attempt is recorded, including skipped cycles and runs still in flight."
      />

      <CardBody className="space-y-3 border-b border-zinc-200 dark:border-zinc-800">
        <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
          <FilterLinks
            label="Status"
            paramKey="status"
            pathname={PATHNAME}
            searchParams={params}
            active={activeStatus}
            resetKeys={["page", "runId"]}
            options={[
              { label: "All", value: null, params: { status: null, failuresOnly: null } },
              {
                label: "Failures",
                value: "failures",
                title: "Failed and partial runs — the operations view",
                params: { failuresOnly: "true", status: null },
              },
              ...STATUSES.map((option) => ({
                label: humanizeEnum(option),
                value: option,
                params: { status: option, failuresOnly: null },
              })),
            ]}
          />

          <FilterLinks
            label="Source"
            paramKey="dataSourceId"
            pathname={PATHNAME}
            searchParams={params}
            active={dataSourceIdRaw ?? null}
            resetKeys={["page", "runId"]}
            options={sourceOptions}
          />
        </div>
      </CardBody>

      {!runsResult.ok ? (
        <CardBody>
          <ApiErrorPanel error={runsResult.error} label="The collection log" />
        </CardBody>
      ) : runsResult.data.items.length === 0 ? (
        <CardBody>
          <EmptyState
            title="No runs match these filters"
            description="Clear the filters to see every recorded attempt."
          />
        </CardBody>
      ) : (
        <>
          <Table caption="Collection runs">
            <thead>
              <tr>
                <Th numeric>Run</Th>
                <Th>Source</Th>
                <Th>Started</Th>
                <Th numeric>Duration</Th>
                <Th>Status</Th>
                <Th numeric>Fetched</Th>
                <Th numeric>Inserted</Th>
                <Th numeric>Revised</Th>
                <Th numeric>Rejected</Th>
                <Th>Failure</Th>
              </tr>
            </thead>
            <tbody>
              {runsResult.data.items.map((run) => (
                <Tr key={run.collectionRunId}>
                  <Td numeric>
                    <span className="font-mono text-xs">
                      {run.collectionRunId}
                    </span>
                  </Td>
                  <Td>
                    <span className="font-mono text-xs">{run.sourceCode}</span>
                    <p className="mt-0.5 text-xs text-zinc-500 dark:text-zinc-400">
                      {humanizeEnum(run.triggerType)}
                      {run.attempt > 1 ? ` · attempt ${run.attempt}` : ""}
                    </p>
                  </Td>
                  <Td>{formatTimestamp(run.startedAtUtc)}</Td>
                  <Td numeric>{formatDuration(run.durationMs)}</Td>
                  <Td>
                    <CollectionStatusBadge status={run.status} />
                  </Td>
                  <Td numeric>{formatCount(run.observationsFetched)}</Td>
                  <Td numeric>{formatCount(run.observationsInserted)}</Td>
                  <Td numeric>
                    {run.observationsRevised > 0 ? (
                      <Badge tone="info">
                        {formatCount(run.observationsRevised)}
                      </Badge>
                    ) : (
                      formatCount(run.observationsRevised)
                    )}
                  </Td>
                  <Td numeric>
                    {run.observationsRejected > 0 ? (
                      <Badge tone="warning">
                        {formatCount(run.observationsRejected)}
                      </Badge>
                    ) : (
                      formatCount(run.observationsRejected)
                    )}
                  </Td>
                  <Td>
                    {run.failureCategory ? (
                      <>
                        <Badge tone="negative">
                          {humanizeEnum(run.failureCategory)}
                        </Badge>
                        {run.errorMessage ? (
                          <p
                            className="mt-1 max-w-xs truncate font-mono text-xs text-zinc-500 dark:text-zinc-400"
                            title={run.errorMessage}
                          >
                            {run.errorMessage}
                          </p>
                        ) : null}
                      </>
                    ) : (
                      <span className="text-zinc-400 dark:text-zinc-600">—</span>
                    )}
                  </Td>
                </Tr>
              ))}
            </tbody>
          </Table>

          <Pagination
            result={runsResult.data}
            pathname={PATHNAME}
            searchParams={params}
            itemLabel="runs"
          />
        </>
      )}
    </Card>
  );
}
