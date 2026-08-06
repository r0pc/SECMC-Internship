/**
 * The one place this app talks to the backend (SOW 4.2 — the two deploy independently and share
 * nothing but HTTP).
 *
 * Calls are made from Server Components, so the browser never issues a cross-origin request and
 * the API's CORS allow-list only matters for anything later moved client-side. Responses are
 * fetched fresh on every render: a dashboard whose numbers are quietly cached is indistinguishable
 * from one whose numbers have not changed, which is the failure this platform exists to make
 * visible.
 */

import type {
  CollectionRunDto,
  CollectionRunStatus,
  DashboardSummaryDto,
  DataSourceDto,
  Dataset,
  IsoDate,
  IsoUtcTimestamp,
  ObservationDto,
  PagedResult,
  PeriodType,
  ProblemDetails,
  SeriesDto,
  SeriesKpiDto,
  SortDirection,
  SourceHealthDto,
  TrendGranularity,
  TrendSeriesDto,
} from "@/types/api";

/** Mirrors the API's own default when `NEXT_PUBLIC_API_BASE_URL` is absent. */
const DEFAULT_API_BASE_URL = "http://localhost:5063";

export const API_BASE_URL = (
  process.env.NEXT_PUBLIC_API_BASE_URL ?? DEFAULT_API_BASE_URL
).replace(/\/+$/, "");

/**
 * A failed call, carrying the API's own ProblemDetails.
 *
 * The API answers every failure in one shape (RFC 9457), so the UI has one error to render rather
 * than one per endpoint. `status` is 0 when the request never reached the API at all — the API
 * being down is a different thing to say than "the API said no".
 */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;
  readonly url: string;

  constructor(status: number, problem: ProblemDetails, url: string) {
    super(problem.detail ?? problem.title ?? `Request to ${url} failed.`);
    this.name = "ApiError";
    this.status = status;
    this.problem = problem;
    this.url = url;
  }

  /** True when the API could not be reached, as opposed to answering with an error. */
  get isUnreachable(): boolean {
    return this.status === 0;
  }
}

type QueryValue =
  | string
  | number
  | boolean
  | readonly string[]
  | null
  | undefined;

/**
 * Builds the query string, dropping anything unset so a URL only carries parameters the caller
 * actually chose. Arrays are joined with commas — the API parses `?seriesKeys=cpi,sofr` and
 * repeated keys identically, and commas keep the URL readable in a bookmark.
 */
function toQueryString(params: Record<string, QueryValue> | undefined): string {
  if (!params) {
    return "";
  }

  const search = new URLSearchParams();

  for (const [key, value] of Object.entries(params)) {
    if (value === null || value === undefined || value === "") {
      continue;
    }

    search.set(key, Array.isArray(value) ? value.join(",") : String(value));
  }

  const query = search.toString();
  return query ? `?${query}` : "";
}

async function readProblem(response: Response, url: string): Promise<ApiError> {
  let problem: ProblemDetails = {
    title: response.statusText || "Request failed",
    status: response.status,
  };

  try {
    const body = (await response.json()) as ProblemDetails;

    if (body && typeof body === "object") {
      problem = { ...problem, ...body };
    }
  } catch {
    // A non-JSON error body (a proxy's HTML page, an empty 502) leaves the defaults above, which
    // still name the status. Losing the body is better than masking the status code with a
    // parse failure.
  }

  return new ApiError(response.status, problem, url);
}

async function apiFetch<T>(
  path: string,
  params?: Record<string, QueryValue>,
): Promise<T> {
  const url = `${API_BASE_URL}${path}${toQueryString(params)}`;

  let response: Response;

  try {
    response = await fetch(url, {
      headers: { Accept: "application/json" },
      // Explicit even though Next 16 does not cache fetches by default: every figure on these
      // pages is an operational reading, and a stale one is worse than a slow one.
      cache: "no-store",
    });
  } catch (cause) {
    // Carries the transport reason forward — ECONNREFUSED and a TLS failure both surface as a
    // thrown fetch, and the fix is different for each.
    const reason = cause instanceof Error ? cause.message : String(cause);

    throw new ApiError(
      0,
      {
        title: "API unreachable",
        detail:
          `Could not reach the Data Intelligence API at ${API_BASE_URL} (${reason}). ` +
          "Check that it is running and that NEXT_PUBLIC_API_BASE_URL points at it.",
      },
      url,
    );
  }

  if (!response.ok) {
    throw await readProblem(response, url);
  }

  return (await response.json()) as T;
}

/** A call that either produced data or an error, rather than one that threw. */
export type ApiResult<T> =
  | { readonly ok: true; readonly data: T }
  | { readonly ok: false; readonly error: ApiError };

/**
 * Runs a call and returns its outcome instead of throwing.
 *
 * Used per panel. A dashboard reads from several endpoints, and one of them failing should cost
 * that panel and no more — an unreachable collection log must not blank the KPIs that loaded fine.
 * Anything not an `ApiError` is a bug in this app rather than a bad response, so it is rethrown.
 */
export async function attempt<T>(call: Promise<T>): Promise<ApiResult<T>> {
  try {
    return { ok: true, data: await call };
  } catch (error) {
    if (error instanceof ApiError) {
      return { ok: false, error };
    }

    throw error;
  }
}

// ---------------------------------------------------------------------------
// Dashboard (FR-10)
// ---------------------------------------------------------------------------

/** `windowDays` is clamped to 1–365 by the API; 30 is its default. */
export function getDashboardSummary(windowDays?: number) {
  return apiFetch<DashboardSummaryDto>("/api/dashboard/summary", { windowDays });
}

export function getKpis(seriesKeys: readonly string[]) {
  return apiFetch<SeriesKpiDto[]>("/api/dashboard/kpis", { seriesKeys });
}

export interface TrendRequest {
  seriesKeys: readonly string[];
  from?: IsoDate;
  to?: IsoDate;
  granularity?: TrendGranularity;
}

export function getTrend({ seriesKeys, from, to, granularity }: TrendRequest) {
  return apiFetch<TrendSeriesDto[]>("/api/dashboard/trend", {
    seriesKeys,
    from,
    to,
    granularity,
  });
}

// ---------------------------------------------------------------------------
// Catalogue (FR-11)
// ---------------------------------------------------------------------------

export interface SeriesListRequest {
  dataSourceId?: number;
  dataset?: Dataset;
  search?: string;
  includeLatest?: boolean;
  page?: number;
  pageSize?: number;
}

export function getSeriesList(options: SeriesListRequest = {}) {
  return apiFetch<PagedResult<SeriesDto>>("/api/series", { ...options });
}

export function getSeries(seriesKey: string) {
  return apiFetch<SeriesDto>(`/api/series/${encodeURIComponent(seriesKey)}`);
}

export interface ObservationsRequest {
  from?: IsoDate;
  to?: IsoDate;
  periodType?: PeriodType;
  includeRevisions?: boolean;
  asOfUtc?: IsoUtcTimestamp;
  sort?: SortDirection;
  page?: number;
  pageSize?: number;
}

export function getObservations(
  seriesKey: string,
  options: ObservationsRequest = {},
) {
  return apiFetch<PagedResult<ObservationDto>>(
    `/api/series/${encodeURIComponent(seriesKey)}/observations`,
    { ...options },
  );
}

// ---------------------------------------------------------------------------
// Sources (FR-7)
// ---------------------------------------------------------------------------

export function getSources() {
  return apiFetch<DataSourceDto[]>("/api/sources");
}

export function getSource(dataSourceId: number) {
  return apiFetch<DataSourceDto>(`/api/sources/${dataSourceId}`);
}

// ---------------------------------------------------------------------------
// Collection log (FR-2)
// ---------------------------------------------------------------------------

export interface CollectionRunsRequest {
  dataSourceId?: number;
  status?: CollectionRunStatus;
  failuresOnly?: boolean;
  fromUtc?: IsoUtcTimestamp;
  toUtc?: IsoUtcTimestamp;
  page?: number;
  pageSize?: number;
}

export function getCollectionRuns(options: CollectionRunsRequest = {}) {
  return apiFetch<PagedResult<CollectionRunDto>>("/api/collection/runs", {
    ...options,
  });
}

export function getCollectionRun(collectionRunId: number) {
  return apiFetch<CollectionRunDto>(`/api/collection/runs/${collectionRunId}`);
}

export function getCollectionHealth(windowDays?: number) {
  return apiFetch<SourceHealthDto[]>("/api/collection/health", { windowDays });
}
