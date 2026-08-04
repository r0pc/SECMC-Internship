/*==============================================================================
  Data Intelligence Platform — database schema (Phase 3 deliverable)
  Target: Microsoft SQL Server 2019+ (tested syntax: 2019 / 2022 / Azure SQL)

  Scope assumption
  ----------------
  Exactly ONE designated data source (SOW 0.1, [DATA SOURCE — TBD]). The source
  is therefore modelled as a single configuration ROW (collect.SourceConfig,
  constrained to one row) rather than as a dimension table. If a second source
  is ever approved, the change is: drop the singleton CHECK, add SourceId to
  core.Item and collect.CollectionRun, and widen core.Item's natural key to
  (SourceId, SourceKey). Nothing else in this schema changes.

  Because the source is still TBD, the measured facts live in
  core.ItemSnapshot's typed "measure" columns (renamed to real business names
  once the source is signed off) plus core.ItemSnapshotAttribute for fields that
  do not warrant their own column. This keeps Phase 4 unblocked without guessing.

  Requirement traceability
  ------------------------
  FR-2  collect.CollectionRun records every attempt, its outcome and failure
        category, so the scheduler logs failures instead of crashing.
  FR-3  Dedup: core.Item.SourceKey is unique; core.ItemSnapshot is unique per
        (ItemId, CollectionRunId), so re-running a cycle cannot double-insert.
  FR-4  No snapshot is ever updated or overwritten — history is append-only.
  FR-5  Normalised: source -> run -> item -> snapshot, with lookup tables for
        category and attribute metadata.
  FR-6  Every snapshot carries CollectedAtUtc (and a derived date key).
  FR-9  sec.AppUser / sec.Role / sec.UserRole back API authn/authz.
  NFR   Scalability: all unique keys on the large tables lead with or include
        CollectedAtUtc, so a partition scheme can be applied later without
        redesign (see "Partitioning" at the foot of this script).
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
  collect.SourceConfig — the single designated data source (SOW 0.1).
  Singleton by construction: SourceConfigId is fixed at 1.
------------------------------------------------------------------------------*/
CREATE TABLE collect.SourceConfig
(
    SourceConfigId      TINYINT         NOT NULL,
    Name                NVARCHAR(100)   NOT NULL,
    BaseUrl             NVARCHAR(500)   NOT NULL,
    CollectionUrl       NVARCHAR(1000)  NOT NULL,
    -- 60 = hourly per FR-1. An interval rather than a cron expression: the requirement is a
    -- fixed cadence, and a cron parser would be a dependency bought for one expression.
    CollectionIntervalMinutes SMALLINT  NOT NULL CONSTRAINT DF_SourceConfig_Interval DEFAULT 60,
    RequestTimeoutSec   SMALLINT        NOT NULL CONSTRAINT DF_SourceConfig_Timeout DEFAULT 30,
    MaxRetries          TINYINT         NOT NULL CONSTRAINT DF_SourceConfig_Retries DEFAULT 3,
    UserAgent           NVARCHAR(250)   NULL,
    -- Compliance (SOW 3): evidence that robots.txt / ToS were checked.
    RobotsTxtCheckedAtUtc DATETIME2(3)  NULL,
    IsEnabled           BIT             NOT NULL CONSTRAINT DF_SourceConfig_Enabled DEFAULT 1,
    CreatedAtUtc        DATETIME2(3)    NOT NULL CONSTRAINT DF_SourceConfig_Created DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc        DATETIME2(3)    NULL,
    CONSTRAINT PK_SourceConfig       PRIMARY KEY (SourceConfigId),
    CONSTRAINT CK_SourceConfig_Single CHECK (SourceConfigId = 1),
    CONSTRAINT CK_SourceConfig_Timeout CHECK (RequestTimeoutSec BETWEEN 1 AND 300)
);
GO

/*------------------------------------------------------------------------------
  collect.CollectionRun — one row per collection attempt (FR-1, FR-2).
  Status/FailureCategory are CHECK-constrained strings rather than lookup
  tables: the value sets are fixed by code (EF Core enums) and never edited by
  users, so a lookup table would add a join without adding integrity.
------------------------------------------------------------------------------*/
CREATE TABLE collect.CollectionRun
(
    CollectionRunId     BIGINT          IDENTITY(1,1) NOT NULL,
    -- Idempotency key: the scheduled hour this run satisfies. Lets a retry be
    -- recognised as the same logical cycle (supports FR-3).
    ScheduledForUtc     DATETIME2(0)    NOT NULL,
    Attempt             TINYINT         NOT NULL CONSTRAINT DF_CollectionRun_Attempt DEFAULT 1,
    TriggerType         VARCHAR(20)     NOT NULL CONSTRAINT DF_CollectionRun_Trigger DEFAULT 'Scheduled',
    StartedAtUtc        DATETIME2(3)    NOT NULL CONSTRAINT DF_CollectionRun_Started DEFAULT SYSUTCDATETIME(),
    CompletedAtUtc      DATETIME2(3)    NULL,
    DurationMs          AS DATEDIFF_BIG(MILLISECOND, StartedAtUtc, CompletedAtUtc),
    Status              VARCHAR(20)     NOT NULL CONSTRAINT DF_CollectionRun_Status DEFAULT 'Running',
    RequestUrl          NVARCHAR(1000)  NOT NULL,
    HttpStatusCode      SMALLINT        NULL,
    RecordsFetched      INT             NOT NULL CONSTRAINT DF_CollectionRun_Fetched  DEFAULT 0,
    RecordsInserted     INT             NOT NULL CONSTRAINT DF_CollectionRun_Inserted DEFAULT 0,
    RecordsUnchanged    INT             NOT NULL CONSTRAINT DF_CollectionRun_Unchanged DEFAULT 0,
    RecordsRejected     INT             NOT NULL CONSTRAINT DF_CollectionRun_Rejected DEFAULT 0,
    FailureCategory     VARCHAR(30)     NULL,
    ErrorMessage        NVARCHAR(1000)  NULL,
    ErrorDetail         NVARCHAR(MAX)   NULL,
    AlertSentAtUtc      DATETIME2(3)    NULL,   -- NFR Reliability: alert on failure
    CONSTRAINT PK_CollectionRun PRIMARY KEY (CollectionRunId),
    CONSTRAINT UQ_CollectionRun_Cycle UNIQUE (ScheduledForUtc, Attempt),
    CONSTRAINT CK_CollectionRun_Status CHECK (Status IN
        ('Running','Succeeded','PartialSuccess','Failed','Skipped')),
    CONSTRAINT CK_CollectionRun_Trigger CHECK (TriggerType IN
        ('Scheduled','Manual','Retry','Backfill')),
    CONSTRAINT CK_CollectionRun_Failure CHECK (FailureCategory IS NULL OR FailureCategory IN
        ('Unreachable','Timeout','HttpError','ParseError','LayoutChanged','Validation','Persistence','Unknown')),
    -- A finished run must say why it finished the way it did.
    CONSTRAINT CK_CollectionRun_FailureRequired CHECK
        (Status <> 'Failed' OR FailureCategory IS NOT NULL),
    CONSTRAINT CK_CollectionRun_Completed CHECK
        (CompletedAtUtc IS NULL OR CompletedAtUtc >= StartedAtUtc)
);
GO

CREATE INDEX IX_CollectionRun_StartedAtUtc
    ON collect.CollectionRun (StartedAtUtc DESC)
    INCLUDE (Status, RecordsInserted);

-- Failures are rare; a filtered index keeps the alerting/health query cheap.
CREATE INDEX IX_CollectionRun_Failures
    ON collect.CollectionRun (StartedAtUtc DESC)
    INCLUDE (FailureCategory, ErrorMessage, AlertSentAtUtc)
    WHERE Status IN ('Failed','PartialSuccess');
GO

/*------------------------------------------------------------------------------
  collect.RawPayload — the untouched response, for replay and for diagnosing
  layout changes (Risk 1 in SOW 9). Store with COMPRESS(); read with
  DECOMPRESS(). Retained on a shorter window than curated data — see the
  purge note at the foot of this script.
------------------------------------------------------------------------------*/
CREATE TABLE collect.RawPayload
(
    RawPayloadId        BIGINT          IDENTITY(1,1) NOT NULL,
    CollectionRunId     BIGINT          NOT NULL,
    FetchedAtUtc        DATETIME2(3)    NOT NULL CONSTRAINT DF_RawPayload_Fetched DEFAULT SYSUTCDATETIME(),
    ContentType         NVARCHAR(100)   NULL,
    -- SHA2_256 of the uncompressed body: equal hash on consecutive runs means
    -- the source published nothing new.
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
  core.Category — dimension for dashboard drill-down (FR-11). Self-referencing
  so a two-level source taxonomy needs no schema change.
------------------------------------------------------------------------------*/
CREATE TABLE core.Category
(
    CategoryId          INT             IDENTITY(1,1) NOT NULL,
    ParentCategoryId    INT             NULL,
    Code                NVARCHAR(100)   NOT NULL,   -- as published by the source
    DisplayName         NVARCHAR(200)   NOT NULL,
    SortOrder           SMALLINT        NOT NULL CONSTRAINT DF_Category_Sort DEFAULT 0,
    CreatedAtUtc        DATETIME2(3)    NOT NULL CONSTRAINT DF_Category_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_Category         PRIMARY KEY (CategoryId),
    CONSTRAINT UQ_Category_Code    UNIQUE (Code),
    CONSTRAINT FK_Category_Parent  FOREIGN KEY (ParentCategoryId)
        REFERENCES core.Category (CategoryId),   -- NO ACTION: cycles are rejected
    CONSTRAINT CK_Category_NotSelfParent CHECK (ParentCategoryId <> CategoryId)
);
GO

CREATE INDEX IX_Category_Parent ON core.Category (ParentCategoryId) WHERE ParentCategoryId IS NOT NULL;
GO

/*------------------------------------------------------------------------------
  core.Item — the distinct real-world entity tracked at the source (a listing,
  product, station, ticker... — named once the source is confirmed).
  This is the dedup anchor for FR-3: SourceKey is the source's own stable
  identifier and is unique. Slowly-changing descriptive fields live here;
  everything that moves over time lives in core.ItemSnapshot.
------------------------------------------------------------------------------*/
CREATE TABLE core.Item
(
    ItemId              INT             IDENTITY(1,1) NOT NULL,
    SourceKey           NVARCHAR(200)   NOT NULL,   -- natural key from the source
    Title               NVARCHAR(400)   NOT NULL,
    CategoryId          INT             NULL,
    SourceUrl           NVARCHAR(1000)  NULL,
    FirstSeenRunId      BIGINT          NOT NULL,
    FirstSeenAtUtc      DATETIME2(3)    NOT NULL,
    LastSeenAtUtc       DATETIME2(3)    NOT NULL,
    -- Set to 0 when the item stops appearing at the source. History is kept
    -- (FR-4); the row is never deleted.
    IsActive            BIT             NOT NULL CONSTRAINT DF_Item_IsActive DEFAULT 1,
    RowVersion          ROWVERSION      NOT NULL,   -- optimistic concurrency
    CONSTRAINT PK_Item           PRIMARY KEY (ItemId),
    CONSTRAINT UQ_Item_SourceKey UNIQUE (SourceKey),
    CONSTRAINT FK_Item_Category  FOREIGN KEY (CategoryId) REFERENCES core.Category (CategoryId),
    CONSTRAINT FK_Item_FirstRun  FOREIGN KEY (FirstSeenRunId)
        REFERENCES collect.CollectionRun (CollectionRunId),
    CONSTRAINT CK_Item_SeenOrder CHECK (LastSeenAtUtc >= FirstSeenAtUtc)
);
GO

CREATE INDEX IX_Item_Category ON core.Item (CategoryId, IsActive) INCLUDE (Title);
CREATE INDEX IX_Item_LastSeen ON core.Item (LastSeenAtUtc DESC);
GO

/*------------------------------------------------------------------------------
  core.ItemSnapshot — the fact table. One immutable row per item per collection
  run (FR-4, FR-6). APPEND-ONLY: no UPDATE, no DELETE outside the archival
  policy. The measure columns below are placeholders with real types; they get
  business names at source sign-off. Anything that does not deserve a column
  goes to core.ItemSnapshotAttribute.
------------------------------------------------------------------------------*/
CREATE TABLE core.ItemSnapshot
(
    ItemSnapshotId      BIGINT          IDENTITY(1,1) NOT NULL,
    ItemId              INT             NOT NULL,
    CollectionRunId     BIGINT          NOT NULL,
    CollectedAtUtc      DATETIME2(3)    NOT NULL,   -- FR-6; future partition key
    -- Persisted date key: cheap dashboard grouping and the eventual partition
    -- boundary column. Deterministic, so it is indexable. Derived from a NOT NULL
    -- column, so it can never actually be null; the constraint is left off because
    -- EF Core does not emit it for computed columns, and keeping this script and
    -- the migration byte-identical is worth more than the redundant declaration.
    CollectedDateKey    AS CONVERT(INT, CONVERT(CHAR(8), CollectedAtUtc, 112)) PERSISTED,

    ---- Measures (rename at [DATA SOURCE — TBD] sign-off) --------------------
    PrimaryValue        DECIMAL(18,4)   NULL,       -- e.g. price / rate / score
    SecondaryValue      DECIMAL(18,4)   NULL,       -- e.g. previous / comparison value
    Quantity            INT             NULL,       -- e.g. stock / volume / count
    StatusText          NVARCHAR(100)   NULL,       -- e.g. availability / state
    CurrencyCode        CHAR(3)         NULL,
    PublishedAtUtc      DATETIME2(3)    NULL,       -- source's own timestamp, if any
    --------------------------------------------------------------------------

    -- SHA2_256 over the normalised measure tuple, computed by the collector.
    -- Equal to the previous snapshot's hash => nothing changed this cycle.
    RowHash             BINARY(32)      NOT NULL,
    HasChanged          BIT             NOT NULL CONSTRAINT DF_ItemSnapshot_Changed DEFAULT 1,

    CONSTRAINT PK_ItemSnapshot PRIMARY KEY NONCLUSTERED (ItemSnapshotId, CollectedAtUtc),
    -- FR-3: re-running a cycle cannot create a second row for the same item.
    CONSTRAINT UQ_ItemSnapshot_ItemRun UNIQUE NONCLUSTERED (ItemId, CollectionRunId, CollectedAtUtc),
    CONSTRAINT FK_ItemSnapshot_Item FOREIGN KEY (ItemId)
        REFERENCES core.Item (ItemId),
    CONSTRAINT FK_ItemSnapshot_Run  FOREIGN KEY (CollectionRunId)
        REFERENCES collect.CollectionRun (CollectionRunId),
    CONSTRAINT CK_ItemSnapshot_Quantity CHECK (Quantity IS NULL OR Quantity >= 0)
);
GO

-- Clustered on time first: every dashboard and trend query is date-ranged
-- (NFR Performance, 12-month range < 3 s), and it is the partition-aligned
-- ordering. PAGE compression because snapshots repeat heavily run-over-run.
CREATE CLUSTERED INDEX CIX_ItemSnapshot_CollectedAtUtc
    ON core.ItemSnapshot (CollectedAtUtc, ItemId)
    WITH (DATA_COMPRESSION = PAGE);

-- Per-item time series (drill-down, FR-11) and "latest snapshot" lookups.
CREATE INDEX IX_ItemSnapshot_Item_Time
    ON core.ItemSnapshot (ItemId, CollectedAtUtc DESC)
    INCLUDE (PrimaryValue, SecondaryValue, Quantity, StatusText, HasChanged)
    WITH (DATA_COMPRESSION = PAGE);

CREATE INDEX IX_ItemSnapshot_Run
    ON core.ItemSnapshot (CollectionRunId)
    WITH (DATA_COMPRESSION = PAGE);
GO

/*------------------------------------------------------------------------------
  core.Attribute / core.ItemSnapshotAttribute — typed key/value extension for
  source fields that are not (yet) promoted to columns. Deliberately narrow:
  it absorbs source churn during Phase 4 instead of forcing a migration, and
  anything queried on a dashboard should be promoted to a real column.
------------------------------------------------------------------------------*/
CREATE TABLE core.Attribute
(
    AttributeId         SMALLINT        IDENTITY(1,1) NOT NULL,
    Code                NVARCHAR(100)   NOT NULL,
    DisplayName         NVARCHAR(200)   NOT NULL,
    DataType            VARCHAR(20)     NOT NULL,
    Unit                NVARCHAR(30)    NULL,
    IsActive            BIT             NOT NULL CONSTRAINT DF_Attribute_Active DEFAULT 1,
    CONSTRAINT PK_Attribute      PRIMARY KEY (AttributeId),
    CONSTRAINT UQ_Attribute_Code UNIQUE (Code),
    CONSTRAINT CK_Attribute_Type CHECK (DataType IN ('Text','Number','Date','Boolean'))
);
GO

CREATE TABLE core.ItemSnapshotAttribute
(
    ItemSnapshotId      BIGINT          NOT NULL,
    CollectedAtUtc      DATETIME2(3)    NOT NULL,   -- carried for partition alignment
    AttributeId         SMALLINT        NOT NULL,
    ValueText           NVARCHAR(1000)  NULL,
    ValueNumber         DECIMAL(18,4)   NULL,
    ValueDate           DATETIME2(3)    NULL,
    ValueBool           BIT             NULL,
    CONSTRAINT PK_ItemSnapshotAttribute PRIMARY KEY (ItemSnapshotId, CollectedAtUtc, AttributeId),
    CONSTRAINT FK_ItemSnapshotAttribute_Snapshot FOREIGN KEY (ItemSnapshotId, CollectedAtUtc)
        REFERENCES core.ItemSnapshot (ItemSnapshotId, CollectedAtUtc) ON DELETE CASCADE,
    CONSTRAINT FK_ItemSnapshotAttribute_Attribute FOREIGN KEY (AttributeId)
        REFERENCES core.Attribute (AttributeId),
    -- Exactly one value slot must be populated.
    CONSTRAINT CK_ItemSnapshotAttribute_OneValue CHECK
    (
        (CASE WHEN ValueText   IS NULL THEN 0 ELSE 1 END
       + CASE WHEN ValueNumber IS NULL THEN 0 ELSE 1 END
       + CASE WHEN ValueDate   IS NULL THEN 0 ELSE 1 END
       + CASE WHEN ValueBool   IS NULL THEN 0 ELSE 1 END) = 1
    )
);
GO

CREATE INDEX IX_ItemSnapshotAttribute_Attribute
    ON core.ItemSnapshotAttribute (AttributeId) INCLUDE (ValueNumber, ValueText);
GO

/*------------------------------------------------------------------------------
  core.RejectedRecord — rows the collector parsed but could not validate.
  Keeps bad data out of core.ItemSnapshot while preserving the evidence
  (FR-2, and Risk "target site changes layout").
------------------------------------------------------------------------------*/
CREATE TABLE core.RejectedRecord
(
    RejectedRecordId    BIGINT          IDENTITY(1,1) NOT NULL,
    CollectionRunId     BIGINT          NOT NULL,
    SourceKey           NVARCHAR(200)   NULL,
    RejectedAtUtc       DATETIME2(3)    NOT NULL CONSTRAINT DF_Rejected_At DEFAULT SYSUTCDATETIME(),
    Reason              VARCHAR(30)     NOT NULL,
    ReasonDetail        NVARCHAR(1000)  NULL,
    RawFragment         NVARCHAR(MAX)   NULL,
    CONSTRAINT PK_RejectedRecord PRIMARY KEY (RejectedRecordId),
    CONSTRAINT FK_RejectedRecord_Run FOREIGN KEY (CollectionRunId)
        REFERENCES collect.CollectionRun (CollectionRunId) ON DELETE CASCADE,
    CONSTRAINT CK_RejectedRecord_Reason CHECK (Reason IN
        ('MissingField','TypeMismatch','OutOfRange','DuplicateKey','SchemaDrift','Unknown'))
);
GO

CREATE INDEX IX_RejectedRecord_Run ON core.RejectedRecord (CollectionRunId, RejectedAtUtc DESC);
GO

/*==============================================================================
  3. SECURITY (FR-9)
  Minimal first-party identity tables. If ASP.NET Core Identity is adopted in
  Phase 4, replace sec.AppUser/Role/UserRole with the AspNet* tables and keep
  the FKs from ai.* pointing at AspNetUsers.Id.
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
  Every generated statement is logged BEFORE execution, so a query that is
  rejected by validation is still on the record.
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
    UserId              INT             NOT NULL,   -- denormalised: audit must
                                                    -- survive session cleanup
    AskedAtUtc          DATETIME2(3)    NOT NULL CONSTRAINT DF_Query_Asked DEFAULT SYSUTCDATETIME(),
    QuestionText        NVARCHAR(2000)  NOT NULL,   -- FR-13

    -- FR-14 / auditability: the statement exactly as the model produced it.
    GeneratedSql        NVARCHAR(MAX)   NULL,
    SqlParametersJson   NVARCHAR(MAX)   NULL,       -- parameterised values
    ValidationOutcome   VARCHAR(20)     NOT NULL CONSTRAINT DF_Query_Validation DEFAULT 'Pending',
    ValidationDetail    NVARCHAR(1000)  NULL,       -- why it was blocked

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
    ClientIpHash        BINARY(32)      NULL,       -- hashed, not raw (Security)

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
    -- Nothing executes unless validation approved it (Risk: unsafe AI SQL).
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

/*------------------------------------------------------------------------------
  ai.AssistantFeedback — thumbs up/down per answer. Feeds the SOW 11.2 test
  question set and the 90% acceptance criterion.
------------------------------------------------------------------------------*/
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
  The AI assistant is granted SELECT on these views only (least privilege) —
  it never sees sec.* or ai.*, which removes a whole class of prompt-injection
  data exposure and shrinks the schema the model must reason about.
==============================================================================*/

-- Latest snapshot per item — the KPI tiles and the tabular report (FR-10).
CREATE VIEW analytics.vw_ItemCurrent
AS
SELECT  i.ItemId,
        i.SourceKey,
        i.Title,
        c.Code            AS CategoryCode,
        c.DisplayName     AS CategoryName,
        i.IsActive,
        s.CollectedAtUtc,
        s.PrimaryValue,
        s.SecondaryValue,
        s.Quantity,
        s.StatusText,
        s.CurrencyCode
FROM    core.Item AS i
LEFT JOIN core.Category AS c ON c.CategoryId = i.CategoryId
CROSS APPLY (
        SELECT TOP (1) sn.CollectedAtUtc, sn.PrimaryValue, sn.SecondaryValue,
                       sn.Quantity, sn.StatusText, sn.CurrencyCode
        FROM   core.ItemSnapshot AS sn
        WHERE  sn.ItemId = i.ItemId
        ORDER  BY sn.CollectedAtUtc DESC
) AS s;
GO

-- Daily rollup for trend charts over long ranges (NFR Performance).
CREATE VIEW analytics.vw_ItemDaily
AS
SELECT  s.ItemId,
        s.CollectedDateKey,
        CONVERT(date, s.CollectedAtUtc) AS CollectedDate,
        COUNT_BIG(*)        AS SnapshotCount,
        MIN(s.PrimaryValue) AS MinPrimaryValue,
        MAX(s.PrimaryValue) AS MaxPrimaryValue,
        AVG(s.PrimaryValue) AS AvgPrimaryValue,
        MAX(s.Quantity)     AS MaxQuantity
FROM    core.ItemSnapshot AS s
GROUP BY s.ItemId, s.CollectedDateKey, CONVERT(date, s.CollectedAtUtc);
GO

-- Collection health: the ≥99% / rolling-30-day reliability NFR.
CREATE VIEW analytics.vw_CollectionHealth
AS
SELECT  CONVERT(date, r.StartedAtUtc) AS RunDate,
        COUNT_BIG(*)                                                     AS TotalRuns,
        SUM(CASE WHEN r.Status = 'Succeeded' THEN 1 ELSE 0 END)          AS SucceededRuns,
        SUM(CASE WHEN r.Status = 'Failed'    THEN 1 ELSE 0 END)          AS FailedRuns,
        CONVERT(DECIMAL(5,2),
            100.0 * SUM(CASE WHEN r.Status = 'Succeeded' THEN 1 ELSE 0 END)
                  / NULLIF(COUNT_BIG(*), 0))                             AS SuccessRatePct,
        SUM(r.RecordsInserted)                                           AS RecordsInserted,
        AVG(r.DurationMs)                                                AS AvgDurationMs
FROM    collect.CollectionRun AS r
WHERE   r.StartedAtUtc >= DATEADD(day, -30, SYSUTCDATETIME())
  AND   r.Status <> 'Running'
GROUP BY CONVERT(date, r.StartedAtUtc);
GO

/*==============================================================================
  6. LEAST-PRIVILEGE ROLES
  The API connects as di_app. The AI assistant executes its validated SQL on a
  SEPARATE connection as di_ai_readonly, which can only read analytics.* —
  a destructive statement that somehow passes validation still cannot run.
==============================================================================*/
CREATE ROLE di_app;
CREATE ROLE di_ai_readonly;
GO

GRANT SELECT, INSERT, UPDATE ON SCHEMA::core    TO di_app;
GRANT SELECT, INSERT, UPDATE ON SCHEMA::collect TO di_app;
GRANT SELECT, INSERT, UPDATE ON SCHEMA::ai      TO di_app;
GRANT SELECT, INSERT, UPDATE ON SCHEMA::sec     TO di_app;
GRANT SELECT                  ON SCHEMA::analytics TO di_app;

GRANT SELECT ON analytics.vw_ItemCurrent      TO di_ai_readonly;
GRANT SELECT ON analytics.vw_ItemDaily        TO di_ai_readonly;
GRANT SELECT ON analytics.vw_CollectionHealth TO di_ai_readonly;
DENY  SELECT ON SCHEMA::sec TO di_ai_readonly;
DENY  SELECT ON SCHEMA::ai  TO di_ai_readonly;
GO

/*==============================================================================
  7. SEED — the single data source (SOW 0.1)
------------------------------------------------------------------------------
  Left commented deliberately. The Worker upserts this row from its
  Collection:* configuration at startup, so seeding it here would just create a
  second source of truth that drifts. The statement is kept for reference and
  for standing up a database by hand.
==============================================================================*/
-- INSERT INTO collect.SourceConfig
--     (SourceConfigId, Name, BaseUrl, CollectionUrl, CollectionIntervalMinutes, IsEnabled)
-- VALUES
--     (1, N'[DATA SOURCE - TBD]', N'https://example.invalid',
--         N'https://example.invalid/data', 60, 0);
-- GO

/*==============================================================================
  8. NOTES FOR PHASE 4
------------------------------------------------------------------------------
  Partitioning (NFR Scalability). Not applied now — one hourly source will not
  need it for a long while — but the schema is ready: CollectedAtUtc is in the
  PK, the unique key and the clustered index of core.ItemSnapshot, and is
  carried on core.ItemSnapshotAttribute. To enable, monthly:

      CREATE PARTITION FUNCTION PF_Monthly (DATETIME2(3))
          AS RANGE RIGHT FOR VALUES ('2026-01-01', '2026-02-01', ...);
      CREATE PARTITION SCHEME PS_Monthly
          AS PARTITION PF_Monthly ALL TO ([PRIMARY]);
      -- then rebuild CIX_ItemSnapshot_CollectedAtUtc ON PS_Monthly(CollectedAtUtc)

  Retention. collect.RawPayload is the bulk of the storage and is diagnostic
  only: purge beyond ~90 days. core.ItemSnapshot is never purged (FR-4).

  Append-only enforcement. core.ItemSnapshot is append-only by convention here.
  If the sponsor wants it enforced in the database, either add an INSTEAD OF
  UPDATE/DELETE trigger, or make it a system-versioned temporal table — note
  that temporal versioning is redundant with the snapshot design, so a trigger
  is the lighter option.

  EF Core. This script is the design of record; generate the equivalent as the
  initial migration (`dotnet ef migrations add InitialSchema`) so the migration
  history stays the deployment mechanism (NFR Maintainability).
==============================================================================*/
