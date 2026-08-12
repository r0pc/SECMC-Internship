/**
 * The API contract, mirrored in TypeScript.
 *
 * Hand-written rather than generated, because the API's OpenAPI document is only served in
 * Development (`/openapi/v1.json`) and a build must not depend on a running backend. Every type
 * here has a counterpart in `DataIntelligence.Core/Dtos`; enums are serialised as their names
 * (`JsonStringEnumConverter` in `Program.cs`), so they are string unions rather than numbers.
 *
 * Two date shapes arrive, and they mean different things:
 *  - `IsoDate` — a `DateOnly`, e.g. `"2026-06-01"`. The period a number describes.
 *  - `IsoUtcTimestamp` — a `DateTime` written with an explicit `Z`. When the platform acted.
 */

/** `DateOnly` — `yyyy-MM-dd`. */
export type IsoDate = string;

/** `DateTime`, round-trip format, always UTC. See the API's `UtcDateTimeConverter`. */
export type IsoUtcTimestamp = string;

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

export type Dataset = "Cpi" | "Sofr";

export type SeriesFrequency =
  | "BusinessDaily"
  | "Daily"
  | "Weekly"
  | "Monthly"
  | "Quarterly"
  | "Semiannual"
  | "Annual";

export type SeasonalAdjustment =
  | "SeasonallyAdjusted"
  | "NotSeasonallyAdjusted"
  | "NotApplicable";

/** The length of the period a CPI figure describes. Null for SOFR. */
export type PeriodType = "Month" | "Semiannual" | "Annual";

export type TrendGranularity = "Auto" | "Point" | "Month" | "Quarter" | "Year";

export type SortDirection = "Ascending" | "Descending";

export type CollectionRunStatus =
  | "Running"
  | "Succeeded"
  | "PartialSuccess"
  | "Failed"
  | "Skipped";

export type CollectionTriggerType = "Scheduled" | "Manual" | "Retry" | "Backfill";

export type CollectionFailureCategory =
  | "Unreachable"
  | "Timeout"
  | "HttpError"
  | "RateLimited"
  | "ParseError"
  | "SchemaChanged"
  | "Validation"
  | "Persistence"
  | "Unknown";

export type SourceAccessMethod = "RestApi" | "Html" | "Csv";

// ---------------------------------------------------------------------------
// Paging
// ---------------------------------------------------------------------------

/** `PagedResult<T>`. The computed members are serialised too, so the pager needs no arithmetic. */
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  /** Total matching rows, ignoring paging. */
  totalCount: number;
  totalPages: number;
  hasPreviousPage: boolean;
  hasNextPage: boolean;
}

// ---------------------------------------------------------------------------
// Catalogue
// ---------------------------------------------------------------------------

export interface DataSourceDto {
  dataSourceId: number;
  code: string;
  name: string;
  publisher: string;
  landingPageUrl: string;
  accessMethod: SourceAccessMethod;
  /** How often the publisher releases — not how often the platform polls. */
  publicationCadence: string;
  collectionIntervalMinutes: number;
  requestTimeoutSec: number;
  maxRetries: number;
  userAgent: string | null;
  /** Compliance evidence for the dashboard footer (SOW 3). */
  termsOfUseUrl: string | null;
  requiresApiKey: boolean;
  isEnabled: boolean;
  seriesCount: number;
}

export interface SeriesLatestPointDto {
  referenceDate: IsoDate;
  value: number;
  /** When the platform learned this value (FR-6), not when it was published. */
  collectedAtPkt: IsoUtcTimestamp;
}

export interface SeriesDto {
  seriesKey: string;
  dataset: Dataset;
  dataSourceId: number;
  sourceCode: string;
  publisherCode: string;
  title: string;
  /** Verbatim from the publisher. Axis labels must use it; values are never rescaled. */
  unit: string;
  decimalPlaces: number;
  frequency: SeriesFrequency;
  seasonalAdjustment: SeasonalAdjustment;
  sourceUrl: string;
  latest: SeriesLatestPointDto | null;
}

// ---------------------------------------------------------------------------
// Observations and trends
// ---------------------------------------------------------------------------

export interface ObservationDto {
  observationId: number;
  seriesKey: string;
  /** The period the number describes. */
  referenceDate: IsoDate;
  periodType: PeriodType | null;
  /** The publisher's own period token, verbatim: `M06`, `M13`. Null for SOFR. */
  periodCode: string | null;
  value: number;
  /** 0 is the first value seen for this period; each correction increments. */
  revisionNumber: number;
  isCurrent: boolean;
  supersededAtPkt: IsoUtcTimestamp | null;
  sourceAnnotation: string | null;
  collectedAtPkt: IsoUtcTimestamp;
  collectionRunId: number;
}

export interface TrendPointDto {
  bucketStart: IsoDate;
  /** Inclusive. Equal to `bucketStart` for unbucketed points. */
  bucketEnd: IsoDate;
  /** Mean of the observations in the bucket. */
  value: number;
  minimum: number;
  maximum: number;
  observationCount: number;
}

export interface TrendSeriesDto {
  seriesKey: string;
  title: string;
  /** Series with different units must not share an axis. */
  unit: string;
  decimalPlaces: number;
  /** The bucket width actually used, after `Auto` was resolved. */
  granularity: TrendGranularity;
  points: TrendPointDto[];
}

export interface SeriesKpiDto {
  seriesKey: string;
  title: string;
  unit: string;
  decimalPlaces: number;
  frequency: SeriesFrequency;
  seasonalAdjustment: SeasonalAdjustment;
  /** Null when nothing has been collected for this series yet. */
  latest: SeriesLatestPointDto | null;
  previousValue: number | null;
  previousReferenceDate: IsoDate | null;
  changeFromPrevious: number | null;
  percentChangeFromPrevious: number | null;
  /** The most recent release at or before one year prior — not an exact-date match. */
  yearAgoValue: number | null;
  yearAgoReferenceDate: IsoDate | null;
  changeFromYearAgo: number | null;
  /** For CPI, the inflation rate as normally quoted. */
  percentChangeFromYearAgo: number | null;
}

// ---------------------------------------------------------------------------
// Collection
// ---------------------------------------------------------------------------

export interface CollectionRunDto {
  collectionRunId: number;
  dataSourceId: number;
  sourceCode: string;
  scheduledForPkt: IsoUtcTimestamp;
  attempt: number;
  triggerType: CollectionTriggerType;
  startedAtPkt: IsoUtcTimestamp;
  completedAtPkt: IsoUtcTimestamp | null;
  durationMs: number | null;
  status: CollectionRunStatus;
  httpStatusCode: number | null;
  observationsFetched: number;
  observationsInserted: number;
  /** Counted apart from inserts: a revision means a published figure moved. */
  observationsRevised: number;
  observationsUnchanged: number;
  observationsRejected: number;
  failureCategory: CollectionFailureCategory | null;
  errorMessage: string | null;
}

export interface SourceHealthDto {
  dataSourceId: number;
  sourceCode: string;
  name: string;
  isEnabled: boolean;
  windowDays: number;
  totalRuns: number;
  succeededRuns: number;
  partialRuns: number;
  failedRuns: number;
  /** Null when the window holds no runs — which is not the same state as 100%. */
  successRatePercent: number | null;
  lastRunAtPkt: IsoUtcTimestamp | null;
  lastSuccessAtPkt: IsoUtcTimestamp | null;
  lastRunStatus: CollectionRunStatus | null;
  lastFailureCategory: CollectionFailureCategory | null;
  lastErrorMessage: string | null;
  /** Non-zero means collection is broken now, not merely at some point in the window. */
  consecutiveFailures: number;
}

export interface DashboardSummaryDto {
  sourceCount: number;
  seriesCount: number;
  /**
   * Reported per dataset rather than summed: a CPI row is a month and a SOFR row is a business
   * day, so one total would only invite the reader to compare them.
   */
  cpiObservationCount: number;
  sofrObservationCount: number;
  earliestCpiMonth: IsoDate | null;
  latestCpiMonth: IsoDate | null;
  earliestSofrDate: IsoDate | null;
  latestSofrDate: IsoDate | null;
  lastCollectionAtPkt: IsoUtcTimestamp | null;
  sources: SourceHealthDto[];
}

// ---------------------------------------------------------------------------
// AI query assistant (FR-13 – FR-16)
// ---------------------------------------------------------------------------

/**
 * Why a generated statement was or was not allowed to run.
 *
 * `NotADataQuestion` is separate from `RejectedNoSql` on purpose: both produce no SQL, but a
 * greeting is not a finding and a question the schema cannot answer might be. Only the second
 * belongs in a reviewer's queue.
 *
 * `RejectedUnreadableResponse` is separated from `RejectedNoSql` for the opposite reason — not
 * because it matters less, but because it points somewhere else. `RejectedNoSql` says the model
 * judged the question unanswerable from the published views; this one says its reply could not be
 * read at all, which is a fault in the platform rather than a gap in the schema.
 */
export type AssistantValidationOutcome =
  | "Pending"
  | "Approved"
  | "RejectedNotSelect"
  | "RejectedForbiddenObject"
  | "RejectedSyntax"
  | "RejectedComplexity"
  | "RejectedNoSql"
  | "NotADataQuestion"
  | "RejectedUnreadableResponse";

export type AssistantExecutionStatus =
  | "Succeeded"
  | "Failed"
  | "Timeout"
  | "Cancelled";

/**
 * Which model answers a question.
 *
 * Named for where the model runs rather than for who made it — the gateway behind `Cloud` is a
 * server-side setting precisely so it can change without a code change, and a name taken from
 * today's provider would be wrong the first time it did. What does not change is whether the
 * question leaves the machine.
 */
export type AssistantModelChoice = "Cloud" | "Local";

export interface AskQuestionRequest {
  /** Continues an existing conversation. Omit to start one. */
  sessionId?: string;
  question: string;

  /** Defaults to `Cloud` server-side when omitted. */
  model?: AssistantModelChoice;
}

/**
 * One answer, with everything needed to judge it.
 *
 * The SQL, its parameters and the model's explanation travel with the answer rather than being
 * hidden behind a toggle in the API: an answer whose query cannot be inspected is a number the
 * reader has to take on trust, which is the opposite of what this platform is for.
 */
export interface AssistantAnswerDto {
  assistantQueryId: number;
  sessionId: string;
  questionText: string;

  validationOutcome: AssistantValidationOutcome;

  /** Null when validation rejected the query before it ran. */
  generatedSql: string | null;

  /** Values bound to the placeholders in `generatedSql`, never inlined into it. */
  sqlParameters: Record<string, unknown> | null;

  /** The model's own account of what the query does. */
  explanation: string | null;

  wasExecuted: boolean;
  executionStatus: AssistantExecutionStatus | null;

  /** The natural-language answer, or an explanation of why there is none. */
  answerText: string;

  /** Result rows, capped by the API. Column order is whatever the query selected. */
  rows: Record<string, unknown>[] | null;

  resultRowCount: number | null;

  /** Which model answered — always known, since it is chosen before the question is sent. */
  modelChoice: AssistantModelChoice;

  /** The model id as its gateway spells it. Null if the turn never reached a model. */
  modelName: string | null;
}

// ---------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------

/** RFC 9457. The API returns this shape for every failure, including validation problems. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  /** Present on 400s produced by `Results.ValidationProblem`. */
  errors?: Record<string, string[]>;
}

/** One of the caller's past conversations, as the resume list shows it. */
export interface AssistantSessionSummaryDto {
  sessionId: string;
  startedAtPkt: IsoUtcTimestamp;
  lastActivityAtPkt: IsoUtcTimestamp;
  turnCount: number;
  /** The conversation's first question, used as its title. Null if it somehow has none. */
  title: string | null;
  /**
   * Tokens the whole conversation has cost the model. Null when none of its turns reported
   * usage — unknown, which is not the same as free, so it must not be rendered as 0.
   */
  totalTokens: number | null;
}

/** One exchange in a replayed conversation. Narrower than a live answer: it has no result rows. */
export interface AssistantTranscriptTurnDto {
  assistantQueryId: number;
  askedAtPkt: IsoUtcTimestamp;
  question: string;
  answer: string | null;
  outcome: AssistantValidationOutcome;
  generatedSql: string | null;
  sqlParameters: Record<string, unknown> | null;
  explanation: string | null;
  wasExecuted: boolean;
  resultRowCount: number | null;

  /**
   * Which model answered this turn. Null on turns recorded before the choice existed — the
   * transcript is a document with no migration step, so an older turn genuinely does not say.
   */
  modelChoice: AssistantModelChoice | null;

  modelName: string | null;
}

/** A past conversation, in full. */
export interface AssistantTranscriptDto {
  sessionId: string;
  startedAtPkt: IsoUtcTimestamp;
  lastActivityAtPkt: IsoUtcTimestamp;
  turns: AssistantTranscriptTurnDto[];
}

// ---------------------------------------------------------------------------
// Authentication (FR-9)
// ---------------------------------------------------------------------------

/**
 * The three access levels, most privileged first.
 *
 * Mirrors `PlatformRoles` in `DataIntelligence.Core/Security`, and the order matters: the API
 * returns a user's roles in it, so `roles[0]` is the highest one they hold.
 */
export type PlatformRole = "Administrator" | "Analyst" | "Viewer";

/** The signed-in caller, as the API reads them out of their token. */
export interface AuthenticatedUserDto {
  userId: number;
  email: string;
  displayName: string;
  roles: PlatformRole[];
}

/** What a correct password buys: the token, when it dies, and who the caller is. */
export interface LoginResponse {
  accessToken: string;
  expiresAtUtc: IsoUtcTimestamp;
  user: AuthenticatedUserDto;
}

/** One account, for the administrator's user list. */
export interface UserDto {
  userId: number;
  email: string;
  displayName: string;
  roles: PlatformRole[];
  isActive: boolean;
  createdAtPkt: IsoUtcTimestamp;
  /** Null until they have signed in once. */
  lastLoginAtPkt: IsoUtcTimestamp | null;
}

export interface CreateUserRequest {
  email: string;
  displayName: string;
  password: string;
  /** Empty grants Viewer — the API's own default, not this app's. */
  roles: PlatformRole[];
}

/** Omitted fields are left unchanged. The email is not editable: it is the login. */
export interface UpdateUserRequest {
  displayName?: string;
  roles?: PlatformRole[];
  isActive?: boolean;
}
