import type { Metadata } from "next";
import Link from "next/link";

import { Badge } from "@/components/badges";
import { Card, CardBody, CardHeader, PageHeader, Stat } from "@/components/card";
import { ApiErrorPanel, EmptyState } from "@/components/states";
import { attempt, getSources } from "@/lib/api";
import { formatCount, humanizeEnum } from "@/lib/format";

export const metadata: Metadata = {
  title: "Sources",
  description:
    "The publishers the platform collects from, their polling settings, and their terms of use.",
};

/**
 * The data sources (FR-7, SOW 3 — Compliance).
 *
 * Read-only here. The API allows the polling settings to be edited, but not until authentication
 * is in place (FR-9): every endpoint is currently anonymous, and a page that lets any visitor
 * disable collection is not a page worth shipping first.
 */
export default async function SourcesPage() {
  const result = await attempt(getSources());

  return (
    <div className="space-y-6">
      <PageHeader
        title="Data sources"
        description={
          <>
            Both publishers expose an official JSON API, so the platform consumes
            those rather than scraping HTML (SOW 9). Endpoint, HTTP method and
            access method are fixed by the adapter compiled against each
            publisher&rsquo;s contract and are not editable.
          </>
        }
      />

      {!result.ok ? (
        <ApiErrorPanel error={result.error} label="The source list" />
      ) : result.data.length === 0 ? (
        <EmptyState
          title="No sources registered"
          description="The collector seeds its sources on first run."
        />
      ) : (
        <div className="grid gap-6 lg:grid-cols-2">
          {result.data.map((source) => (
            <Card key={source.dataSourceId}>
              <CardHeader
                title={source.name}
                hint={
                  <>
                    {source.publisher} · published {source.publicationCadence}
                  </>
                }
                action={
                  <div className="flex items-center gap-2">
                    {source.isEnabled ? (
                      <Badge tone="positive">Enabled</Badge>
                    ) : (
                      <Badge tone="warning">Disabled</Badge>
                    )}
                    <Badge>{humanizeEnum(source.accessMethod)}</Badge>
                  </div>
                }
              />
              <CardBody className="space-y-5">
                <dl className="grid grid-cols-2 gap-5 sm:grid-cols-4">
                  <Stat
                    label="Poll interval"
                    value={`${formatCount(source.collectionIntervalMinutes)} min`}
                  />
                  <Stat
                    label="Timeout"
                    value={`${formatCount(source.requestTimeoutSec)} s`}
                  />
                  <Stat
                    label="Max retries"
                    value={formatCount(source.maxRetries)}
                  />
                  <Stat
                    label="Series"
                    value={formatCount(source.seriesCount)}
                    hint={
                      <Link
                        href={`/series?dataSourceId=${source.dataSourceId}`}
                        className="underline"
                      >
                        browse
                      </Link>
                    }
                  />
                </dl>

                {source.userAgent ? (
                  <div>
                    <p className="text-xs font-medium uppercase tracking-wide text-zinc-500 dark:text-zinc-400">
                      User agent
                    </p>
                    <p className="mt-1 font-mono text-xs break-words text-zinc-600 dark:text-zinc-400">
                      {source.userAgent}
                    </p>
                  </div>
                ) : null}

                <div className="flex flex-wrap items-center gap-x-5 gap-y-2 border-t border-zinc-100 pt-4 text-sm dark:border-zinc-900">
                  <a
                    href={source.landingPageUrl}
                    target="_blank"
                    rel="noreferrer"
                    className="text-zinc-700 underline hover:text-zinc-900 dark:text-zinc-300 dark:hover:text-zinc-100"
                  >
                    Publisher page ↗
                  </a>
                  {/* Compliance evidence, not decoration (SOW 3). Its absence is worth showing. */}
                  {source.termsOfUseUrl ? (
                    <a
                      href={source.termsOfUseUrl}
                      target="_blank"
                      rel="noreferrer"
                      className="text-zinc-700 underline hover:text-zinc-900 dark:text-zinc-300 dark:hover:text-zinc-100"
                    >
                      Terms of use ↗
                    </a>
                  ) : (
                    <span className="text-zinc-400 dark:text-zinc-500">
                      No terms-of-use link recorded
                    </span>
                  )}
                  {source.requiresApiKey ? (
                    <Badge tone="info" title="Key is held server-side, never in this app">
                      API key required
                    </Badge>
                  ) : null}
                </div>
              </CardBody>
            </Card>
          ))}
        </div>
      )}
    </div>
  );
}
