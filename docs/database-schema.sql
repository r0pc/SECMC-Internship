/*==============================================================================
  Data Intelligence Platform — database schema (Phase 3 deliverable)
  Target: Microsoft SQL Server 2019+ (validated on 2022 / 2025)

  Revision: rewritten for the confirmed data sources (SOW 0.1 sign-off).

  Designated sources
  ------------------
    1. US Consumer Price Index      — U.S. Bureau of Labor Statistics
                                      https://www.bls.gov/data/home.htm
                                      API: POST https://api.bls.gov/publicAPI/v2/timeseries/data/
    2. Secured Overnight Financing  — Federal Reserve Bank of New York
       Rate (SOFR)                    https://www.newyorkfed.org/markets/reference-rates/sofr
                                      API: GET https://markets.newyorkfed.org/api/rates/secured/sofr/...

  What changed from the single-source draft, and why
  --------------------------------------------------
  1. TWO SOURCES. collect.SourceConfig (one row, enforced by a CHECK) becomes
     collect.DataSource (one row per publisher). DataSourceId is carried on
     CollectionRun and Series, and the series natural key widens to
     (DataSourceId, SeriesCode). This is the migration the previous draft
     described; it is now executed rather than anticipated.

  2. TIME SERIES, NOT LISTINGS. core.Item / core.ItemSnapshot modelled "an entity
     observed repeatedly", with PrimaryValue/SecondaryValue placeholders pending
     sign-off. Both confirmed sources publish economic time series instead, so
     they become core.Series / core.Observation with a single, named Value.

  3. BI-TEMPORAL. An observation has two independent dates: ReferenceDate (the
     period the number describes — June 2026, or 31 Jul 2026) and CollectedAtUtc
     (when we learned it). These are not interchangeable: CPI for June exists
     before, during and after we collect it, and is revised afterwards.

  4. REVISIONS ARE FIRST CLASS. Both publishers revise. The NY Fed API returns a
     "revisionIndicator" field; BLS footnotes carry "R" and reissue seasonally
     adjusted series annually. Each vintage is a row (RevisionNumber), the newest
     is flagged IsCurrent, and nothing is ever overwritten (FR-4). Dashboards read
     current vintages; the revision history stays queryable underneath.

  5. ONE MEASURE PER SERIES. A single SOFR API record carries six measures (rate,
     volume, and the 1st/25th/75th/99th percentiles). Rather than six nullable
     columns on a shared fact table — which would be null for every CPI row — each
     measure is its own series. core.Observation stays (Series, Date) -> Value,
     fully normalised, and a new measure needs a row rather than a migration.

  6. THE KEY/VALUE EXTENSION TABLES ARE GONE. core.Attribute and
     core.ItemSnapshotAttribute hedged against an unknown source's unknown fields.
     Both payload shapes are now known and small, so the hedge costs clarity
     without buying anything.

  Requirement traceability
  ------------------------
  FR-2  collect.CollectionRun records every attempt, per source, with a failure
        category, so the scheduler logs failures instead of crashing.
  FR-3  Dedup: UQ_Series_Code; UQ_Observation_Vintage; and the collector only
        writes a new vintage when the value actually changed.
  FR-4  Append-only. A revision adds a row; it never updates one.
  FR-5  Normalised: source -> run -> series -> observation, with a category
        dimension for CPI's item hierarchy.
  FR-6  Every observation carries CollectedAtUtc alongside its ReferenceDate.
  FR-9  sec.AppUser / sec.Role / sec.UserRole back API authn/authz.
  NFR   Scalability: ReferenceDate leads the clustered index and appears in every
        unique key, so the fact table can be partitioned on it without redesign.
  NFR   Auditability: ai.AssistantQuery logs every AI-generated SQL statement,
        whether it passed validation, and whether it was executed.
==============================================================================*/

/*------------------------------------------------------------------------------
  0. Session options and schemas
  QUOTED_IDENTIFIER / ANSI_NULLS must be ON to create the filtered indexes and
  the index on the persisted computed column below. Set here rather than left
  to the client: sqlcmd defaults QUOTED_IDENTIFIER to OFF.
------------------------------------------------------------------------------*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID('collect')   IS NULL EXEC('CREATE SCHEMA collect');    -- ingestion
IF SCHEMA_ID('core')      IS NULL EXEC('CREATE SCHEMA core');       -- curated data
IF SCHEMA_ID('ai')        IS NULL EXEC('CREATE SCHEMA ai');         -- assistant audit
IF SCHEMA_ID('sec')       IS NULL EXEC('CREATE SCHEMA sec');        -- identity
IF SCHEMA_ID('analytics') IS NULL EXEC('CREATE SCHEMA analytics');  -- read models
GO

/*==============================================================================
  1. COLLECTION LAYER
==============================================================================*/

/*------------------------------------------------------------------------------
  collect.DataSource — one row per designated publisher (SOW 0.1).
  Seeded in section 7; rows are reference data, not user-managed configuration.
------------------------------------------------------------------------------*/
CREATE TABLE collect.DataSource
(
    DataSourceId        TINYINT         NOT NULL,
    Code                VARCHAR(20)     NOT NULL,   -- stable key used in config and logs
    Name                NVARCHAR(100)   NOT NULL,
    Publisher           NVARCHAR(100)   NOT NULL,
    LandingPageUrl      NVARCHAR(500)   NOT NULL,   -- human-facing page, for documentation
    ApiEndpoint         NVARCHAR(1000)  NOT NULL,

    -- Both confirmed sources publish official JSON APIs, so the platform consumes
    -- those rather than scraping HTML (SOW 9: "prefer an official API if one exists").
    -- HtmlDocument is retained as a value so a fallback importer stays modelled.
    AccessMethod        VARCHAR(20)     NOT NULL CONSTRAINT DF_DataSource_Access DEFAULT 'RestApi',
    HttpMethod          VARCHAR(6)      NOT NULL CONSTRAINT DF_DataSource_Method DEFAULT 'GET',
    RequiresApiKey      BIT             NOT NULL CONSTRAINT DF_DataSource_Key DEFAULT 0,

    -- How often the publisher releases, which is not how often we poll. CPI is
    -- monthly; SOFR is published each business day at ~08:00 ET. Recorded so the
    -- dashboards can say "next release expected" rather than implying staleness.
    PublicationCadence  VARCHAR(20)     NOT NULL,
    CollectionIntervalMinutes SMALLINT  NOT NULL CONSTRAINT DF_DataSource_Interval DEFAULT 60,

    RequestTimeoutSec   SMALLINT        NOT NULL CONSTRAINT DF_DataSource_Timeout DEFAULT 30,
    MaxRetries          TINYINT         NOT NULL CONSTRAINT DF_DataSource_Retries DEFAULT 3,
    UserAgent           NVARCHAR(250)   NULL,

    -- Compliance evidence (SOW 3). Both publishers are US federal/public bodies
    -- offering these APIs for programmatic use; the terms URL is recorded so the
    -- claim is auditable rather than assumed.
    TermsOfUseUrl       NVARCHAR(500)   NULL,
    RobotsTxtCheckedAtUtc DATETIME2(3)  NULL,

    IsEnabled           BIT             NOT NULL CONSTRAINT DF_DataSource_Enabled DEFAULT 1,
    CreatedAtUtc        DATETIME2(3)    NOT NULL CONSTRAINT DF_DataSource_Created DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc        DATETIME2(3)    NULL,

    CONSTRAINT PK_DataSource        PRIMARY KEY (DataSourceId),
    CONSTRAINT UQ_DataSource_Code   UNIQUE (Code),
    CONSTRAINT CK_DataSource_Access CHECK (AccessMethod IN ('RestApi','Html','Csv')),
    CONSTRAINT CK_DataSource_Method CHECK (HttpMethod IN ('GET','POST')),
    CONSTRAINT CK_DataSource_Cadence CHECK (PublicationCadence IN
        ('BusinessDaily','Daily','Weekly','Monthly','Quarterly','Annual','Irregular')),
    CONSTRAINT CK_DataSource_Timeout CHECK (RequestTimeoutSec BETWEEN 1 AND 300),
    CONSTRAINT CK_DataSource_Interval CHECK (CollectionIntervalMinutes BETWEEN 1 AND 1440)
);
GO

/*------------------------------------------------------------------------------
  collect.CollectionRun — one row per collection attempt, per source (FR-1).
  Every attempt is recorded, including failures, so the scheduler logs rather
  than crashes (FR-2) and the rolling 30-day success rate is answerable in SQL.
------------------------------------------------------------------------------*/
CREATE TABLE collect.CollectionRun
(
    CollectionRunId     BIGINT          IDENTITY(1,1) NOT NULL,
    DataSourceId        TINYINT         NOT NULL,

    -- The scheduled cycle this run satisfies. With Attempt, this is the run's
    -- idempotency key: a retry of the 10:00 CPI cycle is (1, 10:00, 2), which
    -- keeps retries distinguishable from a fresh cycle and from the other source.
    ScheduledForUtc     DATETIME2(0)    NOT NULL,
    Attempt             TINYINT         NOT NULL CONSTRAINT DF_CollectionRun_Attempt DEFAULT 1,
    TriggerType         VARCHAR(20)     NOT NULL CONSTRAINT DF_CollectionRun_Trigger DEFAULT 'Scheduled',

    StartedAtUtc        DATETIME2(3)    NOT NULL CONSTRAINT DF_CollectionRun_Started DEFAULT SYSUTCDATETIME(),
    CompletedAtUtc      DATETIME2(3)    NULL,
    DurationMs          AS DATEDIFF_BIG(MILLISECOND, StartedAtUtc, CompletedAtUtc),

    Status              VARCHAR(20)     NOT NULL CONSTRAINT DF_CollectionRun_Status DEFAULT 'Running',
    RequestUrl          NVARCHAR(1000)  NOT NULL,
    HttpStatusCode      SMALLINT        NULL,

    ObservationsFetched INT             NOT NULL CONSTRAINT DF_CollectionRun_Fetched  DEFAULT 0,
    ObservationsInserted INT            NOT NULL CONSTRAINT DF_CollectionRun_Inserted DEFAULT 0,
    ObservationsRevised INT             NOT NULL CONSTRAINT DF_CollectionRun_Revised  DEFAULT 0,
    ObservationsUnchanged INT           NOT NULL CONSTRAINT DF_CollectionRun_Unchanged DEFAULT 0,
    ObservationsRejected INT            NOT NULL CONSTRAINT DF_CollectionRun_Rejected DEFAULT 0,

    FailureCategory     VARCHAR(30)     NULL,
    ErrorMessage        NVARCHAR(1000)  NULL,
    ErrorDetail         NVARCHAR(MAX)   NULL,
    AlertSentAtUtc      DATETIME2(3)    NULL,   -- NFR Reliability: alert on failure

    CONSTRAINT PK_CollectionRun PRIMARY KEY (CollectionRunId),
    CONSTRAINT UQ_CollectionRun_Cycle UNIQUE (DataSourceId, ScheduledForUtc, Attempt),
    CONSTRAINT FK_CollectionRun_Source FOREIGN KEY (DataSourceId)
        REFERENCES collect.DataSource (DataSourceId),
    CONSTRAINT CK_CollectionRun_Status CHECK (Status IN
        ('Running','Succeeded','PartialSuccess','Failed','Skipped')),
    CONSTRAINT CK_CollectionRun_Trigger CHECK (TriggerType IN
        ('Scheduled','Manual','Retry','Backfill')),
    CONSTRAINT CK_CollectionRun_Failure CHECK (FailureCategory IS NULL OR FailureCategory IN
        ('Unreachable','Timeout','HttpError','RateLimited','ParseError','SchemaChanged',
         'Validation','Persistence','Unknown')),
    -- A finished run must say why it finished the way it did.
    CONSTRAINT CK_CollectionRun_FailureRequired CHECK
        (Status <> 'Failed' OR FailureCategory IS NOT NULL),
    CONSTRAINT CK_CollectionRun_Completed CHECK
        (CompletedAtUtc IS NULL OR CompletedAtUtc >= StartedAtUtc)
);
GO

CREATE INDEX IX_CollectionRun_StartedAtUtc
    ON collect.CollectionRun (StartedAtUtc DESC)
    INCLUDE (DataSourceId, Status, ObservationsInserted);

-- Failures are rare; a filtered index keeps the health and alerting queries cheap.
CREATE INDEX IX_CollectionRun_Failures
    ON collect.CollectionRun (StartedAtUtc DESC)
    INCLUDE (DataSourceId, FailureCategory, ErrorMessage, AlertSentAtUtc)
    WHERE Status IN ('Failed','PartialSuccess');
GO

/*------------------------------------------------------------------------------
  collect.RawPayload — the untouched API response for a run, stored compressed.
  Diagnostic: lets a parse failure be reproduced and a cycle re-parsed without
  re-requesting, which matters when BLS limits unregistered callers to a small
  daily query budget.
------------------------------------------------------------------------------*/
CREATE TABLE collect.RawPayload
(
    RawPayloadId        BIGINT          IDENTITY(1,1) NOT NULL,
    CollectionRunId     BIGINT          NOT NULL,
    FetchedAtUtc        DATETIME2(3)    NOT NULL CONSTRAINT DF_RawPayload_Fetched DEFAULT SYSUTCDATETIME(),
    ContentType         NVARCHAR(100)   NULL,

    -- SHA-256 of the uncompressed body. An unchanged hash between consecutive
    -- runs means the publisher released nothing new — the cheapest short-circuit
    -- there is, and the common case when polling monthly data hourly.
    ContentHash         BINARY(32)      NOT NULL,
    SizeBytes           INT             NOT NULL,
    CompressedContent   VARBINARY(MAX)  NOT NULL,

    CONSTRAINT PK_RawPayload PRIMARY KEY (RawPayloadId),
    CONSTRAINT FK_RawPayload_Run FOREIGN KEY (CollectionRunId)
        REFERENCES collect.CollectionRun (CollectionRunId) ON DELETE CASCADE,
    CONSTRAINT CK_RawPayload_Size CHECK (SizeBytes >= 0)
);
GO

CREATE INDEX IX_RawPayload_Run  ON collect.RawPayload (CollectionRunId);
CREATE INDEX IX_RawPayload_Hash ON collect.RawPayload (ContentHash, FetchedAtUtc DESC);
GO

/*==============================================================================
  2. CURATED LAYER
==============================================================================*/

/*------------------------------------------------------------------------------
  core.SeriesCategory — grouping for dashboard drill-down (FR-11).
  Self-referencing because CPI's item structure is a hierarchy: All items ->
  Food and beverages -> Food -> Food at home.
------------------------------------------------------------------------------*/
CREATE TABLE core.SeriesCategory
(
    CategoryId          INT             IDENTITY(1,1) NOT NULL,
    ParentCategoryId    INT             NULL,
    Code                NVARCHAR(100)   NOT NULL,
    DisplayName         NVARCHAR(200)   NOT NULL,
    SortOrder           SMALLINT        NOT NULL CONSTRAINT DF_SeriesCategory_Sort DEFAULT 0,
    CreatedAtUtc        DATETIME2(3)    NOT NULL CONSTRAINT DF_SeriesCategory_Created DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_SeriesCategory        PRIMARY KEY (CategoryId),
    CONSTRAINT UQ_SeriesCategory_Code   UNIQUE (Code),
    CONSTRAINT FK_SeriesCategory_Parent FOREIGN KEY (ParentCategoryId)
        REFERENCES core.SeriesCategory (CategoryId),  -- NO ACTION: cycles are rejected
    CONSTRAINT CK_SeriesCategory_NotSelfParent CHECK (ParentCategoryId <> CategoryId)
);
GO

CREATE INDEX IX_SeriesCategory_Parent ON core.SeriesCategory (ParentCategoryId)
    WHERE ParentCategoryId IS NOT NULL;
GO

/*------------------------------------------------------------------------------
  core.Series — one measured quantity tracked through time.
  The dedup anchor for FR-3: (DataSourceId, SeriesCode) is unique, so a re-run
  matches the existing series instead of creating a second one.
------------------------------------------------------------------------------*/
CREATE TABLE core.Series
(
    SeriesId            INT             IDENTITY(1,1) NOT NULL,
    DataSourceId        TINYINT         NOT NULL,

    -- The publisher's own identifier where it has one (BLS: 'CUUR0000SA0').
    -- Where one API record carries several measures, as with SOFR, the code is
    -- assigned by this platform ('SOFR_VOL') and SourceFieldPath records the
    -- field it comes from. IsSourceAssignedCode says which is which, so nobody
    -- later mistakes our identifier for the publisher's.
    SeriesCode          NVARCHAR(100)   NOT NULL,
    IsSourceAssignedCode BIT            NOT NULL CONSTRAINT DF_Series_SourceCode DEFAULT 1,
    SourceFieldPath     NVARCHAR(200)   NULL,

    Title               NVARCHAR(400)   NOT NULL,
    CategoryId          INT             NULL,

    -- The unit the values are stored in, verbatim from the publisher. Values are
    -- NEVER rescaled on the way in: SOFR volume stays in billions because that is
    -- what the API publishes, and rescaling silently is how a chart ends up wrong
    -- by a factor of a thousand with nothing in the data to show it.
    Unit                NVARCHAR(60)    NOT NULL,
    DecimalPlaces       TINYINT         NULL,   -- as published, for display fidelity

    Frequency           VARCHAR(20)     NOT NULL,
    SeasonalAdjustment  VARCHAR(24)     NOT NULL CONSTRAINT DF_Series_Seasonal DEFAULT 'NotApplicable',

    SourceUrl           NVARCHAR(1000)  NULL,
    FirstSeenRunId      BIGINT          NULL,
    FirstSeenAtUtc      DATETIME2(3)    NULL,
    LastSeenAtUtc       DATETIME2(3)    NULL,
    IsActive            BIT             NOT NULL CONSTRAINT DF_Series_IsActive DEFAULT 1,
    RowVersion          ROWVERSION      NOT NULL,

    CONSTRAINT PK_Series        PRIMARY KEY (SeriesId),
    CONSTRAINT UQ_Series_Code   UNIQUE (DataSourceId, SeriesCode),
    CONSTRAINT FK_Series_Source FOREIGN KEY (DataSourceId)
        REFERENCES collect.DataSource (DataSourceId),
    CONSTRAINT FK_Series_Category FOREIGN KEY (CategoryId)
        REFERENCES core.SeriesCategory (CategoryId),
    CONSTRAINT FK_Series_FirstRun FOREIGN KEY (FirstSeenRunId)
        REFERENCES collect.CollectionRun (CollectionRunId),
    CONSTRAINT CK_Series_Frequency CHECK (Frequency IN
        ('BusinessDaily','Daily','Weekly','Monthly','Quarterly','Semiannual','Annual')),
    CONSTRAINT CK_Series_Seasonal CHECK (SeasonalAdjustment IN
        ('SeasonallyAdjusted','NotSeasonallyAdjusted','NotApplicable')),
    CONSTRAINT CK_Series_SeenOrder CHECK
        (LastSeenAtUtc IS NULL OR FirstSeenAtUtc IS NULL OR LastSeenAtUtc >= FirstSeenAtUtc),
    -- A platform-assigned code must say where it came from, or the mapping is lost.
    CONSTRAINT CK_Series_FieldPath CHECK
        (IsSourceAssignedCode = 1 OR SourceFieldPath IS NOT NULL)
);
GO

CREATE INDEX IX_Series_Category ON core.Series (CategoryId, IsActive) INCLUDE (Title);
CREATE INDEX IX_Series_Source   ON core.Series (DataSourceId, IsActive) INCLUDE (SeriesCode, Title);

-- EF Core indexes every foreign key by convention and offers no way to suppress it. Declared
-- here so this script and the generated migration stay byte-identical; on a table of a few
-- hundred series rows it costs nothing either way.
CREATE INDEX IX_Series_FirstSeenRunId ON core.Series (FirstSeenRunId);
GO

/*------------------------------------------------------------------------------
  core.Observation — the fact table. One row per (series, reference period,
  vintage). Append-only: a revision inserts a new row and clears IsCurrent on
  the previous one; no value is ever overwritten (FR-4).

  The two dates are independent and both required:
    ReferenceDate  — the period the number describes (2026-06-01 for CPI "M06",
                     2026-07-31 for a SOFR business day). The analytical axis.
    CollectedAtUtc — when this platform learned it (FR-6). The audit axis.
  Asking "what did we believe CPI for June was, on 15 July?" needs both.
------------------------------------------------------------------------------*/
CREATE TABLE core.Observation
(
    ObservationId       BIGINT          IDENTITY(1,1) NOT NULL,
    SeriesId            INT             NOT NULL,

    ReferenceDate       DATE            NOT NULL,   -- period start; future partition key
    -- The period's length, which Frequency alone cannot give: a BLS monthly series
    -- also publishes M13 (annual average) and S01/S02 (semiannual) rows, and
    -- averaging those into a monthly trend would double-count.
    PeriodType          VARCHAR(12)     NOT NULL,
    -- The publisher's own period token, kept verbatim for traceability: 'M06', 'M13'.
    SourcePeriodCode    VARCHAR(6)      NULL,

    -- 0 is the first value we saw for this period; each later correction increments.
    RevisionNumber      SMALLINT        NOT NULL CONSTRAINT DF_Observation_Revision DEFAULT 0,
    IsCurrent           BIT             NOT NULL CONSTRAINT DF_Observation_Current DEFAULT 1,
    SupersededAtUtc     DATETIME2(3)    NULL,

    -- Wide enough that a series published in dollars rather than billions cannot
    -- overflow, and precise enough for the SOFR Index, which publishes 8 decimals.
    Value               DECIMAL(28,8)   NOT NULL,

    -- Publisher annotation, verbatim: BLS footnote codes, or the NY Fed's
    -- revisionIndicator. Kept as published rather than interpreted.
    SourceAnnotation    VARCHAR(100)    NULL,

    CollectionRunId     BIGINT          NOT NULL,
    CollectedAtUtc      DATETIME2(3)    NOT NULL,   -- FR-6

    -- Persisted date key for cheap dashboard grouping. Derived from a NOT NULL
    -- column so it is never null; the constraint is omitted because EF Core does
    -- not emit it for computed columns and the two artifacts must stay identical.
    ReferenceDateKey    AS CONVERT(INT, CONVERT(CHAR(8), ReferenceDate, 112)) PERSISTED,

    -- SHA-256 over the value tuple, computed by the collector. Equal to the current
    -- vintage's hash means the publisher reissued the same number, so no row is
    -- written (FR-3) — the common case when polling monthly data every hour.
    RowHash             BINARY(32)      NOT NULL,

    -- ReferenceDate is carried in every unique key so the table can be partitioned
    -- on it later without redesign: a partitioned table requires the partitioning
    -- column in every unique index (NFR Scalability).
    CONSTRAINT PK_Observation PRIMARY KEY NONCLUSTERED (ObservationId, ReferenceDate),
    CONSTRAINT UQ_Observation_Vintage UNIQUE NONCLUSTERED
        (SeriesId, ReferenceDate, RevisionNumber),
    CONSTRAINT FK_Observation_Series FOREIGN KEY (SeriesId)
        REFERENCES core.Series (SeriesId),
    CONSTRAINT FK_Observation_Run FOREIGN KEY (CollectionRunId)
        REFERENCES collect.CollectionRun (CollectionRunId),
    CONSTRAINT CK_Observation_PeriodType CHECK (PeriodType IN
        ('Day','Week','Month','Quarter','Semiannual','Annual')),
    CONSTRAINT CK_Observation_Revision CHECK (RevisionNumber >= 0),
    -- A superseded row is not current, and a current row is not superseded.
    CONSTRAINT CK_Observation_Superseded CHECK
        ((IsCurrent = 1 AND SupersededAtUtc IS NULL)
      OR (IsCurrent = 0 AND SupersededAtUtc IS NOT NULL))
);
GO

-- Clustered on the analytical axis: every dashboard and trend query is a series
-- over a date range, and this is the partition-aligned ordering. Page compression
-- because consecutive observations of an index level share leading digits.
CREATE CLUSTERED INDEX CIX_Observation_Reference
    ON core.Observation (ReferenceDate, SeriesId)
    WITH (DATA_COMPRESSION = PAGE);

-- Exactly one current vintage per (series, period). This is the integrity rule the
-- dashboards depend on: without it a botched revision could double-count a month
-- and no query would reveal it.
CREATE UNIQUE INDEX UQ_Observation_Current
    ON core.Observation (SeriesId, ReferenceDate)
    WHERE IsCurrent = 1;

-- The per-series time series read: KPI tiles, trend charts, and the collector's
-- own "what is the current value for this period?" dedup check.
CREATE INDEX IX_Observation_Series_Reference
    ON core.Observation (SeriesId, ReferenceDate DESC)
    INCLUDE (Value, RowHash, IsCurrent, PeriodType)
    WITH (DATA_COMPRESSION = PAGE);

CREATE INDEX IX_Observation_Run ON core.Observation (CollectionRunId)
    WITH (DATA_COMPRESSION = PAGE);
GO

/*------------------------------------------------------------------------------
  core.RejectedObservation — parsed but unusable. Keeps bad data out of the fact
  table while preserving the evidence; a rejection spike is the earliest signal
  that a publisher changed its payload shape.
------------------------------------------------------------------------------*/
CREATE TABLE core.RejectedObservation
(
    RejectedObservationId BIGINT        IDENTITY(1,1) NOT NULL,
    CollectionRunId     BIGINT          NOT NULL,
    SeriesCode          NVARCHAR(100)   NULL,
    ReferenceDateText   NVARCHAR(50)    NULL,   -- as published; may be why it was rejected
    RejectedAtUtc       DATETIME2(3)    NOT NULL CONSTRAINT DF_Rejected_At DEFAULT SYSUTCDATETIME(),
    Reason              VARCHAR(30)     NOT NULL,
    ReasonDetail        NVARCHAR(1000)  NULL,
    RawFragment         NVARCHAR(MAX)   NULL,

    CONSTRAINT PK_RejectedObservation PRIMARY KEY (RejectedObservationId),
    CONSTRAINT FK_RejectedObservation_Run FOREIGN KEY (CollectionRunId)
        REFERENCES collect.CollectionRun (CollectionRunId) ON DELETE CASCADE,
    CONSTRAINT CK_RejectedObservation_Reason CHECK (Reason IN
        ('MissingField','TypeMismatch','OutOfRange','UnknownSeries','DuplicatePeriod',
         'UnparseablePeriod','SchemaDrift','Unknown'))
);
GO

CREATE INDEX IX_RejectedObservation_Run
    ON core.RejectedObservation (CollectionRunId, RejectedAtUtc DESC);
GO

/*==============================================================================
  3. SECURITY (FR-9)
  Minimal first-party identity tables. If ASP.NET Core Identity is adopted in
  Phase 4, replace these with the AspNet* tables and keep the FKs from ai.*
  pointing at AspNetUsers.Id.
==============================================================================*/
CREATE TABLE sec.AppUser
(
    UserId              INT             IDENTITY(1,1) NOT NULL,
    Email               NVARCHAR(256)   NOT NULL,
    DisplayName         NVARCHAR(150)   NOT NULL,
    PasswordHash        NVARCHAR(500)   NOT NULL,   -- ASP.NET Identity v3 format
    SecurityStamp       UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_AppUser_Stamp DEFAULT NEWID(),
    IsActive            BIT             NOT NULL CONSTRAINT DF_AppUser_Active DEFAULT 1,
    CreatedAtUtc        DATETIME2(3)    NOT NULL CONSTRAINT DF_AppUser_Created DEFAULT SYSUTCDATETIME(),
    LastLoginAtUtc      DATETIME2(3)    NULL,

    CONSTRAINT PK_AppUser       PRIMARY KEY (UserId),
    CONSTRAINT UQ_AppUser_Email UNIQUE (Email)
);
GO

CREATE TABLE sec.Role
(
    RoleId              TINYINT         IDENTITY(1,1) NOT NULL,
    Name                NVARCHAR(50)    NOT NULL,
    Description         NVARCHAR(250)   NULL,

    CONSTRAINT PK_Role      PRIMARY KEY (RoleId),
    CONSTRAINT UQ_Role_Name UNIQUE (Name)
);
GO

CREATE TABLE sec.UserRole
(
    UserId              INT             NOT NULL,
    RoleId              TINYINT         NOT NULL,
    GrantedAtUtc        DATETIME2(3)    NOT NULL CONSTRAINT DF_UserRole_Granted DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_UserRole      PRIMARY KEY (UserId, RoleId),
    CONSTRAINT FK_UserRole_User FOREIGN KEY (UserId) REFERENCES sec.AppUser (UserId) ON DELETE CASCADE,
    CONSTRAINT FK_UserRole_Role FOREIGN KEY (RoleId) REFERENCES sec.Role (RoleId)
);
GO

INSERT INTO sec.Role (Name, Description) VALUES
    (N'Administrator', N'Full access: configuration, user management, all data.'),
    (N'Analyst',       N'Dashboards, drill-down and the AI query assistant.'),
    (N'Viewer',        N'Read-only dashboards.');
GO

/*==============================================================================
  4. AI QUERY ASSISTANT (FR-13 .. FR-17, NFR Auditability)
  Every generated statement is logged BEFORE execution, so a query rejected by
  validation is still on the record.
==============================================================================*/
CREATE TABLE ai.AssistantSession
(
    SessionId           UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Session_Id DEFAULT NEWSEQUENTIALID(),
    UserId              INT             NOT NULL,
    StartedAtUtc        DATETIME2(3)    NOT NULL CONSTRAINT DF_Session_Started DEFAULT SYSUTCDATETIME(),
    LastActivityAtUtc   DATETIME2(3)    NOT NULL CONSTRAINT DF_Session_Activity DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_AssistantSession PRIMARY KEY (SessionId),
    CONSTRAINT FK_AssistantSession_User FOREIGN KEY (UserId) REFERENCES sec.AppUser (UserId)
);
GO

CREATE INDEX IX_AssistantSession_User ON ai.AssistantSession (UserId, StartedAtUtc DESC);
GO

CREATE TABLE ai.AssistantQuery
(
    AssistantQueryId    BIGINT          IDENTITY(1,1) NOT NULL,
    SessionId           UNIQUEIDENTIFIER NOT NULL,
    UserId              INT             NOT NULL,   -- denormalised: the audit trail must
                                                    -- survive session cleanup
    AskedAtUtc          DATETIME2(3)    NOT NULL CONSTRAINT DF_Query_Asked DEFAULT SYSUTCDATETIME(),
    QuestionText        NVARCHAR(2000)  NOT NULL,   -- FR-13

    -- FR-14 / auditability: the statement exactly as the model produced it.
    GeneratedSql        NVARCHAR(MAX)   NULL,
    SqlParametersJson   NVARCHAR(MAX)   NULL,
    ValidationOutcome   VARCHAR(20)     NOT NULL CONSTRAINT DF_Query_Validation DEFAULT 'Pending',
    ValidationDetail    NVARCHAR(1000)  NULL,

    -- FR-15
    WasExecuted         BIT             NOT NULL CONSTRAINT DF_Query_Executed DEFAULT 0,
    ExecutionStatus     VARCHAR(20)     NULL,
    ExecutionMs         INT             NULL,
    ResultRowCount      INT             NULL,
    ExecutionError      NVARCHAR(1000)  NULL,

    AnswerText          NVARCHAR(MAX)   NULL,       -- FR-16
    VisualizationJson   NVARCHAR(MAX)   NULL,       -- FR-17 (stretch) chart config

    ModelName           NVARCHAR(100)   NULL,
    PromptTokens        INT             NULL,
    CompletionTokens    INT             NULL,
    TotalLatencyMs      INT             NULL,
    ClientIpHash        BINARY(32)      NULL,       -- hashed, not raw (SOW 3 — Security)

    CONSTRAINT PK_AssistantQuery PRIMARY KEY (AssistantQueryId),
    CONSTRAINT FK_AssistantQuery_Session FOREIGN KEY (SessionId)
        REFERENCES ai.AssistantSession (SessionId),
    CONSTRAINT FK_AssistantQuery_User FOREIGN KEY (UserId)
        REFERENCES sec.AppUser (UserId),
    CONSTRAINT CK_AssistantQuery_Validation CHECK (ValidationOutcome IN
        ('Pending','Approved','RejectedNotSelect','RejectedForbiddenObject',
         'RejectedSyntax','RejectedComplexity','RejectedNoSql')),
    CONSTRAINT CK_AssistantQuery_Execution CHECK (ExecutionStatus IS NULL OR ExecutionStatus IN
        ('Succeeded','Failed','Timeout','Cancelled')),
    -- Nothing executes unless validation approved it (SOW 9: unsafe AI SQL).
    CONSTRAINT CK_AssistantQuery_NoUnvalidatedRun CHECK
        (WasExecuted = 0 OR ValidationOutcome = 'Approved')
);
GO

CREATE INDEX IX_AssistantQuery_AskedAtUtc ON ai.AssistantQuery (AskedAtUtc DESC);
CREATE INDEX IX_AssistantQuery_User       ON ai.AssistantQuery (UserId, AskedAtUtc DESC);
-- Review queue: everything the validator turned away.
CREATE INDEX IX_AssistantQuery_Rejected   ON ai.AssistantQuery (AskedAtUtc DESC)
    INCLUDE (QuestionText, ValidationOutcome, ValidationDetail)
    WHERE ValidationOutcome <> 'Approved';
GO

CREATE TABLE ai.AssistantFeedback
(
    AssistantQueryId    BIGINT          NOT NULL,
    IsHelpful           BIT             NOT NULL,
    Comment             NVARCHAR(1000)  NULL,
    SubmittedAtUtc      DATETIME2(3)    NOT NULL CONSTRAINT DF_Feedback_At DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_AssistantFeedback PRIMARY KEY (AssistantQueryId),
    CONSTRAINT FK_AssistantFeedback_Query FOREIGN KEY (AssistantQueryId)
        REFERENCES ai.AssistantQuery (AssistantQueryId) ON DELETE CASCADE
);
GO

/*==============================================================================
  5. READ MODELS
  The AI assistant is granted SELECT on these views only (least privilege). It
  never sees sec.* or ai.*, which removes a class of prompt-injection data
  exposure and shrinks the schema the model has to reason about.

  Every view filters to IsCurrent = 1, so an ordinary question gets the current
  vintage. Revision history stays reachable in core.Observation for anyone who
  needs it, which is the right default: "what is CPI for June" should not return
  three different answers.
==============================================================================*/

-- Current-vintage observations, denormalised for querying and charting.
CREATE VIEW analytics.vw_Observation
AS
SELECT  s.SeriesId,
        d.Code              AS SourceCode,
        d.Name              AS SourceName,
        s.SeriesCode,
        s.Title             AS SeriesTitle,
        c.Code              AS CategoryCode,
        c.DisplayName       AS CategoryName,
        s.Unit,
        s.Frequency,
        s.SeasonalAdjustment,
        o.ReferenceDate,
        o.ReferenceDateKey,
        o.PeriodType,
        o.Value,
        o.RevisionNumber,
        o.SourceAnnotation,
        o.CollectedAtUtc
FROM    core.Observation AS o
JOIN    core.Series      AS s ON s.SeriesId = o.SeriesId
JOIN    collect.DataSource AS d ON d.DataSourceId = s.DataSourceId
LEFT JOIN core.SeriesCategory AS c ON c.CategoryId = s.CategoryId
WHERE   o.IsCurrent = 1;
GO

-- Latest value per series: the KPI tiles (FR-10).
CREATE VIEW analytics.vw_SeriesLatest
AS
SELECT  s.SeriesId,
        d.Code          AS SourceCode,
        s.SeriesCode,
        s.Title         AS SeriesTitle,
        s.Unit,
        s.Frequency,
        s.SeasonalAdjustment,
        latest.ReferenceDate,
        latest.PeriodType,
        latest.Value,
        latest.RevisionNumber,
        latest.CollectedAtUtc,
        s.IsActive
FROM    core.Series AS s
JOIN    collect.DataSource AS d ON d.DataSourceId = s.DataSourceId
CROSS APPLY (
        SELECT TOP (1) o.ReferenceDate, o.PeriodType, o.Value,
                       o.RevisionNumber, o.CollectedAtUtc
        FROM   core.Observation AS o
        WHERE  o.SeriesId = s.SeriesId
          AND  o.IsCurrent = 1
        ORDER  BY o.ReferenceDate DESC
) AS latest;
GO

/*------------------------------------------------------------------------------
  Period-over-period and year-over-year change — the headline CPI number is
  year-over-year inflation, not the index level, so the platform computes it
  rather than making every caller rediscover it.

  Joined on an explicit date offset rather than LAG(): a series with a missing
  month, or one that also carries M13 annual rows, would make a positional
  lag silently compare the wrong two periods.
------------------------------------------------------------------------------*/
CREATE VIEW analytics.vw_SeriesChange
AS
SELECT  cur.SeriesId,
        cur.ReferenceDate,
        cur.PeriodType,
        cur.Value,
        prev.Value      AS PreviousPeriodValue,
        CASE WHEN prev.Value IS NULL OR prev.Value = 0 THEN NULL
             ELSE CONVERT(DECIMAL(18,6), (cur.Value - prev.Value) * 100.0 / ABS(prev.Value))
        END             AS PeriodOverPeriodPct,
        yago.Value      AS YearAgoValue,
        CASE WHEN yago.Value IS NULL OR yago.Value = 0 THEN NULL
             ELSE CONVERT(DECIMAL(18,6), (cur.Value - yago.Value) * 100.0 / ABS(yago.Value))
        END             AS YearOverYearPct
FROM    core.Observation AS cur
OUTER APPLY (
        SELECT TOP (1) p.Value
        FROM   core.Observation AS p
        WHERE  p.SeriesId = cur.SeriesId
          AND  p.IsCurrent = 1
          AND  p.PeriodType = cur.PeriodType
          AND  p.ReferenceDate < cur.ReferenceDate
        ORDER  BY p.ReferenceDate DESC
) AS prev
OUTER APPLY (
        SELECT TOP (1) y.Value
        FROM   core.Observation AS y
        WHERE  y.SeriesId = cur.SeriesId
          AND  y.IsCurrent = 1
          AND  y.PeriodType = cur.PeriodType
          AND  y.ReferenceDate = DATEADD(year, -1, cur.ReferenceDate)
) AS yago
WHERE   cur.IsCurrent = 1;
GO

-- Revision history: how a published figure moved after first release.
CREATE VIEW analytics.vw_ObservationRevision
AS
SELECT  s.SeriesCode,
        s.Title         AS SeriesTitle,
        o.ReferenceDate,
        o.RevisionNumber,
        o.Value,
        o.IsCurrent,
        o.SourceAnnotation,
        o.CollectedAtUtc,
        o.SupersededAtUtc
FROM    core.Observation AS o
JOIN    core.Series AS s ON s.SeriesId = o.SeriesId
WHERE   EXISTS (SELECT 1 FROM core.Observation AS r
                WHERE r.SeriesId = o.SeriesId
                  AND r.ReferenceDate = o.ReferenceDate
                  AND r.RevisionNumber > 0);
GO

-- Collection health per source: the >=99% / rolling-30-day reliability NFR.
CREATE VIEW analytics.vw_CollectionHealth
AS
SELECT  d.Code                                                          AS SourceCode,
        d.Name                                                          AS SourceName,
        CONVERT(date, r.StartedAtUtc)                                   AS RunDate,
        COUNT_BIG(*)                                                    AS TotalRuns,
        SUM(CASE WHEN r.Status = 'Succeeded' THEN 1 ELSE 0 END)         AS SucceededRuns,
        SUM(CASE WHEN r.Status = 'Failed'    THEN 1 ELSE 0 END)         AS FailedRuns,
        SUM(CASE WHEN r.Status = 'Skipped'   THEN 1 ELSE 0 END)         AS SkippedRuns,
        CONVERT(DECIMAL(5,2),
            100.0 * SUM(CASE WHEN r.Status = 'Succeeded' THEN 1 ELSE 0 END)
                  / NULLIF(COUNT_BIG(*), 0))                            AS SuccessRatePct,
        SUM(r.ObservationsInserted)                                     AS ObservationsInserted,
        SUM(r.ObservationsRevised)                                      AS ObservationsRevised,
        AVG(r.DurationMs)                                               AS AvgDurationMs
FROM    collect.CollectionRun AS r
JOIN    collect.DataSource AS d ON d.DataSourceId = r.DataSourceId
WHERE   r.StartedAtUtc >= DATEADD(day, -30, SYSUTCDATETIME())
  AND   r.Status <> 'Running'
GROUP BY d.Code, d.Name, CONVERT(date, r.StartedAtUtc);
GO

/*==============================================================================
  6. LEAST-PRIVILEGE ROLES
  The API connects as di_app. The AI assistant executes its validated SQL on a
  SEPARATE connection as di_ai_readonly, which can only read analytics.* — so a
  destructive statement that somehow passes validation still cannot run.
==============================================================================*/
CREATE ROLE di_app;
CREATE ROLE di_ai_readonly;
GO

GRANT SELECT, INSERT, UPDATE ON SCHEMA::core      TO di_app;
GRANT SELECT, INSERT, UPDATE ON SCHEMA::collect   TO di_app;
GRANT SELECT, INSERT, UPDATE ON SCHEMA::ai        TO di_app;
GRANT SELECT, INSERT, UPDATE ON SCHEMA::sec       TO di_app;
GRANT SELECT                  ON SCHEMA::analytics TO di_app;

GRANT SELECT ON analytics.vw_Observation           TO di_ai_readonly;
GRANT SELECT ON analytics.vw_SeriesLatest          TO di_ai_readonly;
GRANT SELECT ON analytics.vw_SeriesChange          TO di_ai_readonly;
GRANT SELECT ON analytics.vw_ObservationRevision   TO di_ai_readonly;
GRANT SELECT ON analytics.vw_CollectionHealth      TO di_ai_readonly;
DENY  SELECT ON SCHEMA::sec TO di_ai_readonly;
DENY  SELECT ON SCHEMA::ai  TO di_ai_readonly;
GO

/*==============================================================================
  7. SEED — the designated sources and their initial series (SOW 0.1)
------------------------------------------------------------------------------
  These are reference data, not user configuration: the platform is commissioned
  against these two publishers. Seeding them here means DataSourceId is stable
  across environments, so configuration and logs can refer to a source by Code.
==============================================================================*/
INSERT INTO collect.DataSource
    (DataSourceId, Code, Name, Publisher, LandingPageUrl, ApiEndpoint,
     AccessMethod, HttpMethod, RequiresApiKey, PublicationCadence,
     CollectionIntervalMinutes, TermsOfUseUrl, IsEnabled)
VALUES
    (1, 'BLS_CPI', N'US Consumer Price Index', N'U.S. Bureau of Labor Statistics',
     N'https://www.bls.gov/data/home.htm',
     N'https://api.bls.gov/publicAPI/v2/timeseries/data/',
     'RestApi', 'POST',
     -- Unregistered v2 calls work but are rate-limited; a registered key raises the
     -- daily budget. Flagged so the sponsor can decide whether to register.
     0, 'Monthly', 60,
     N'https://www.bls.gov/developers/api_faqs.htm', 1),

    (2, 'NYFED_SOFR', N'Secured Overnight Financing Rate', N'Federal Reserve Bank of New York',
     N'https://www.newyorkfed.org/markets/reference-rates/sofr',
     N'https://markets.newyorkfed.org/api/rates/secured/sofr/last/10.json',
     'RestApi', 'GET', 0, 'BusinessDaily', 60,
     N'https://www.newyorkfed.org/markets/reference-rates/terms-of-use-for-selected-rate-data', 1);
GO

INSERT INTO core.SeriesCategory (Code, DisplayName, SortOrder) VALUES
    (N'cpi-headline',   N'CPI — All items',                 10),
    (N'cpi-core',       N'CPI — All items less food and energy', 20),
    (N'sofr-rate',      N'SOFR — Rate',                     30),
    (N'sofr-liquidity', N'SOFR — Volume and distribution',  40);
GO

/*------------------------------------------------------------------------------
  CPI series. SeriesCode is the BLS series ID, so IsSourceAssignedCode = 1.
  Both the seasonally adjusted and unadjusted variants are tracked: SA is the
  right basis for month-over-month change, NSA for year-over-year.
------------------------------------------------------------------------------*/
INSERT INTO core.Series
    (DataSourceId, SeriesCode, IsSourceAssignedCode, Title, CategoryId, Unit,
     DecimalPlaces, Frequency, SeasonalAdjustment, SourceUrl)
SELECT 1, v.SeriesCode, 1, v.Title, c.CategoryId, N'Index 1982-84=100', 3, 'Monthly',
       v.Seasonal, N'https://www.bls.gov/cpi/'
FROM (VALUES
    (N'CUUR0000SA0',    N'CPI-U, All items, US city average, not seasonally adjusted',
        N'cpi-headline', 'NotSeasonallyAdjusted'),
    (N'CUSR0000SA0',    N'CPI-U, All items, US city average, seasonally adjusted',
        N'cpi-headline', 'SeasonallyAdjusted'),
    (N'CUUR0000SA0L1E', N'CPI-U, All items less food and energy, not seasonally adjusted',
        N'cpi-core',     'NotSeasonallyAdjusted'),
    (N'CUSR0000SA0L1E', N'CPI-U, All items less food and energy, seasonally adjusted',
        N'cpi-core',     'SeasonallyAdjusted')
) AS v (SeriesCode, Title, CategoryCode, Seasonal)
JOIN core.SeriesCategory AS c ON c.Code = v.CategoryCode;
GO

/*------------------------------------------------------------------------------
  SOFR series. One API record carries six measures, so each becomes its own
  series with a platform-assigned code (IsSourceAssignedCode = 0) and
  SourceFieldPath naming the JSON field it is read from.
------------------------------------------------------------------------------*/
INSERT INTO core.Series
    (DataSourceId, SeriesCode, IsSourceAssignedCode, SourceFieldPath, Title, CategoryId,
     Unit, DecimalPlaces, Frequency, SeasonalAdjustment, SourceUrl)
SELECT 2, v.SeriesCode, 0, v.FieldPath, v.Title, c.CategoryId, v.Unit, v.Dp,
       'BusinessDaily', 'NotApplicable',
       N'https://www.newyorkfed.org/markets/reference-rates/sofr'
FROM (VALUES
    (N'SOFR',      N'percentRate',          N'SOFR, overnight rate',
        N'sofr-rate',      N'Percent per annum', CONVERT(TINYINT,2)),
    (N'SOFR_VOL',  N'volumeInBillions',     N'SOFR, transaction volume',
        N'sofr-liquidity', N'USD billions',      CONVERT(TINYINT,0)),
    (N'SOFR_P1',   N'percentPercentile1',   N'SOFR, 1st percentile',
        N'sofr-liquidity', N'Percent per annum', CONVERT(TINYINT,2)),
    (N'SOFR_P25',  N'percentPercentile25',  N'SOFR, 25th percentile',
        N'sofr-liquidity', N'Percent per annum', CONVERT(TINYINT,2)),
    (N'SOFR_P75',  N'percentPercentile75',  N'SOFR, 75th percentile',
        N'sofr-liquidity', N'Percent per annum', CONVERT(TINYINT,2)),
    (N'SOFR_P99',  N'percentPercentile99',  N'SOFR, 99th percentile',
        N'sofr-liquidity', N'Percent per annum', CONVERT(TINYINT,2))
) AS v (SeriesCode, FieldPath, Title, CategoryCode, Unit, Dp)
JOIN core.SeriesCategory AS c ON c.Code = v.CategoryCode;
GO

/*==============================================================================
  8. NOTES FOR PHASE 4
------------------------------------------------------------------------------
  Revision handling. Inserting a revision is two statements in one transaction:
  clear IsCurrent (setting SupersededAtUtc) on the existing current row, then
  insert the new vintage with RevisionNumber + 1. UQ_Observation_Current makes a
  mistake here a hard failure rather than a silently duplicated period.

  Polling cadence versus publication cadence. FR-1 specifies hourly collection.
  CPI is monthly and SOFR is business-daily, so the overwhelming majority of
  cycles will find nothing new. That is handled, not wasted: the collector
  compares RowHash and records the run as succeeded with zero inserts, and
  collect.RawPayload.ContentHash short-circuits an unchanged body before parsing.
  Worth confirming with the sponsor whether hourly is still wanted, or whether
  polling aligned to publication (SOFR ~08:00 ET; CPI on the BLS release
  calendar) is a better fit — see the Scope Document refinement.

  Partitioning (NFR Scalability). Not applied: two sources at monthly and daily
  cadence produce thousands of rows a year, not millions. The schema is ready —
  ReferenceDate leads the clustered index and appears in the PK and both unique
  keys. To enable, yearly:

      CREATE PARTITION FUNCTION PF_Yearly (DATE)
          AS RANGE RIGHT FOR VALUES ('2020-01-01', '2021-01-01', ...);
      CREATE PARTITION SCHEME PS_Yearly
          AS PARTITION PF_Yearly ALL TO ([PRIMARY]);
      -- then rebuild CIX_Observation_Reference ON PS_Yearly(ReferenceDate)

  Retention. collect.RawPayload is diagnostic and is the bulk of the storage;
  purge beyond ~90 days. core.Observation is never purged (FR-4).

  Append-only enforcement. core.Observation is append-only by convention, except
  for the IsCurrent/SupersededAtUtc flip that a revision performs. If the sponsor
  wants it enforced in the database, an INSTEAD OF DELETE trigger plus an UPDATE
  trigger restricted to those two columns is the lighter option.

  EF Core. This script is the design of record; the equivalent is generated as a
  migration so the migration history remains the deployment mechanism (NFR
  Maintainability). The two are verified identical by creating a database from
  each and diffing columns, indexes and constraints.
==============================================================================*/
