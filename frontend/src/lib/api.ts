/**
 * The one place this app talks to the backend (SOW 4.2 — the two deploy independently and share
 * nothing but HTTP).
 *
 * Calls are made from Server Components, so the browser never issues a cross-origin request and
 * the API's CORS allow-list only matters for anything later moved client-side. Responses are
 * fetched fresh on every render: a dashboard whose numbers are quietly cached is indistinguishable
 * from one whose numbers have not changed, which is the failure this platform exists to make
 * visible.
 *
 * Since FR-9 every call but the login carries the caller's bearer token, read from the session
 * cookie. Attached here rather than passed down through every function, because a call that
 * forgets it does not fail loudly — it fails as a 401 somewhere in a panel, which reads like the
 * API being down.
 */

import { redirect } from "next/navigation";

import { getAccessToken } from "@/lib/session";
import type {
  AskQuestionRequest,
  AssistantAnswerDto,
  AssistantSessionSummaryDto,
  AssistantTranscriptDto,
  AuthenticatedUserDto,
  CollectionRunDto,
  CollectionRunStatus,
  CreateUserRequest,
  DashboardSummaryDto,
  DataSourceDto,
  Dataset,
  IsoDate,
  IsoUtcTimestamp,
  LoginResponse,
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
  UpdateUserRequest,
  UserDto,
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

/**
 * Where a request whose token was refused is sent.
 *
 * A Route Handler rather than the login page directly, because the cookie has to be removed and a
 * Server Component cannot remove it. Skipping that step would loop: `/login` would see a session
 * cookie, believe the visitor is signed in, and send them back to a page that 401s again.
 */
const SIGNED_OUT_PATH = "/logout?reason=expired";

/**
 * The authorization header for a call, or nothing when signed out.
 *
 * Signed-out calls are still made rather than short-circuited here. Only the API knows which
 * endpoints are public — `/health` is, and the login is — and a frontend that decided for itself
 * would be a second copy of that rule, free to disagree with the first.
 */
async function authorization(): Promise<Record<string, string>> {
  const token = await getAccessToken();

  return token ? { Authorization: `Bearer ${token}` } : {};
}

/**
 * Turns the API's 401 into a redirect rather than an error panel.
 *
 * A 401 here means the token was rejected, and by the time a page is rendering there is exactly
 * one honest reading of that: the session is over. It expired, the account was disabled, or its
 * password or roles changed — all of which the API decides, and none of which this app can see
 * from the token alone. Rendering "Unauthorized" into a panel would tell the reader the platform
 * is broken when the truth is that they need to sign in again.
 *
 * `redirect` throws, which is how it unwinds out of nested calls; `attempt` below rethrows
 * anything that is not an ApiError, so it passes through untouched.
 */
function redirectIfSessionEnded(status: number): void {
  if (status === 401) {
    redirect(SIGNED_OUT_PATH);
  }
}

async function apiFetch<T>(
  path: string,
  params?: Record<string, QueryValue>,
): Promise<T> {
  const url = `${API_BASE_URL}${path}${toQueryString(params)}`;

  let response: Response;

  try {
    response = await fetch(url, {
      headers: { Accept: "application/json", ...(await authorization()) },
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
    redirectIfSessionEnded(response.status);

    throw await readProblem(response, url);
  }

  return (await response.json()) as T;
}

/**
 * A call that sends a JSON body.
 *
 * Separate from `apiFetch` rather than folded into it with an optional body: every other call in
 * this file is a read, and keeping the verbs that change something visibly distinct is worth a
 * little duplication. `204 No Content` is answered with `undefined`, since a body would be a
 * contract violation rather than something to parse.
 */
async function apiSend<T>(
  method: "POST" | "PATCH",
  path: string,
  body: unknown,
  options: { readonly anonymous?: boolean } = {},
): Promise<T> {
  const url = `${API_BASE_URL}${path}`;

  let response: Response;

  try {
    response = await fetch(url, {
      method,
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json",
        // The login is the one call with nothing to present yet. Sending a stale token with it
        // would be harmless but confusing in a log, and asking for one that does not exist is a
        // cookie read for nothing.
        ...(options.anonymous ? {} : await authorization()),
      },
      body: JSON.stringify(body),
      cache: "no-store",
    });
  } catch (cause) {
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
    // Not on the login itself: a 401 there is a wrong password, which the form has to show. Only
    // a call that presented a token and had it refused means the session is over.
    if (!options.anonymous) {
      redirectIfSessionEnded(response.status);
    }

    throw await readProblem(response, url);
  }

  if (response.status === 204) {
    return undefined as T;
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

// ---------------------------------------------------------------------------
// AI query assistant (FR-13 – FR-16)
// ---------------------------------------------------------------------------

/**
 * Asks a question and waits for the whole round trip — question to SQL, validation, execution,
 * results back to the model, answer.
 *
 * Slow by the standards of everything else here: the API budgets 30 seconds for the model and 10
 * for the query, and observed latency is several seconds. The UI has to show that it is working
 * rather than appearing to have frozen.
 */
export function askAssistant(request: AskQuestionRequest) {
  return apiSend<AssistantAnswerDto>("POST", "/api/assistant/ask", request);
}

/** The caller's past conversations, most recently used first. */
export function getAssistantSessions(limit?: number) {
  return apiFetch<AssistantSessionSummaryDto[]>("/api/assistant/sessions", { limit });
}

/** One past conversation, in full, for replaying into the chat. */
export function getAssistantTranscript(sessionId: string) {
  return apiFetch<AssistantTranscriptDto>(`/api/assistant/sessions/${sessionId}`);
}

// ---------------------------------------------------------------------------
// Authentication (FR-9)
// ---------------------------------------------------------------------------

/**
 * Exchanges an email and password for a token.
 *
 * The one call made without one. A 401 from here is a wrong password and belongs on the form, not
 * in a redirect — see the `anonymous` note in `apiSend`.
 */
export function login(email: string, password: string) {
  return apiSend<LoginResponse>(
    "POST",
    "/api/auth/login",
    { email, password },
    { anonymous: true },
  );
}

/**
 * The caller as the API reads them out of their own token.
 *
 * Rarely needed: the same facts are in the session cookie, and reading them there costs no round
 * trip. Worth calling when the question is "is this token still good", because only the API can
 * answer that — it re-reads the account and compares its security stamp.
 */
export function getCurrentUser() {
  return apiFetch<AuthenticatedUserDto>("/api/auth/me");
}

/** Changes the caller's own password. Ends every session, including the one making the call. */
export function changeOwnPassword(currentPassword: string, newPassword: string) {
  return apiSend<void>("POST", "/api/auth/password", {
    currentPassword,
    newPassword,
  });
}

// ---------------------------------------------------------------------------
// User administration (FR-9) — Administrator only, enforced by the API
// ---------------------------------------------------------------------------

export function getUsers() {
  return apiFetch<UserDto[]>("/api/users");
}

export function createUser(request: CreateUserRequest) {
  return apiSend<UserDto>("POST", "/api/users", request);
}

export function updateUser(userId: number, request: UpdateUserRequest) {
  return apiSend<UserDto>("PATCH", `/api/users/${userId}`, request);
}

/** Sets someone else's password, for a colleague who has forgotten theirs. */
export function resetUserPassword(userId: number, newPassword: string) {
  return apiSend<void>("POST", `/api/users/${userId}/password`, { newPassword });
}
