/*==============================================================================
  Data Intelligence Platform — database schema (Phase 3 deliverable)
  Target: Microsoft SQL Server 2019+ (validated on 2022 / 2025)

  Revision: rewritten against the sample extracts in docs/example_data/
            (CPI.csv, SOFR.csv) and the narrowed data scope.

  Scope of the data, as agreed
  ----------------------------
    1. CPI  — exactly one BLS series, CUUR0000SA0
              "All items in U.S. city average, all urban consumers,
               not seasonally adjusted", base 1982-84 = 100.
              Monthly, plus the annual (M13) and semiannual (S01/S02) figures
              BLS publishes alongside it — the Annual / HALF1 / HALF2 columns
              of CPI.csv.
              https://www.bls.gov/data/home.htm
              API: POST https://api.bls.gov/publicAPI/v2/timeseries/data/

    2. SOFR — the Secured Overnight Financing Rate: one row per business day,
              rate type SOFR only. The other four rates in the same CSV download
              (EFFR, OBFR, TGCR, BGCR) are out of scope and are rejected on the
              way in, not stored.
              The scheduled cycle collects the current calendar year; the table
              is not constrained to it, and the collector's backfill loads the
              series from its first published day, 2 April 2018.
              https://www.newyorkfed.org/markets/reference-rates/sofr
              API: GET https://markets.newyorkfed.org/api/rates/secured/sofr/
                       search.json?startDate=&endDate=

  What changed from the generic-series draft, and why
  ---------------------------------------------------
  1. SEPARATE TABLES PER DATASET. core.Series / core.Observation modelled "any
     series from any publisher": a series registry, a category hierarchy, and a
     single (SeriesId, Date) -> Value fact table shared by everything. With the
     scope fixed at one CPI series and one rate, that generality bought nothing
     and cost a join on every query. core.CpiObservation and core.SofrDailyRate
     replace it — each table IS its series, so the series identity lives in the
     table name and in one pinned column, not in a lookup row.

  2. SOFR'S SIX MEASURES ARE SIX COLUMNS AGAIN. Under one shared fact table,
     rate / volume / the four percentiles had to become six separate series or
     six columns that were null for every CPI row. Neither applies now: a SOFR
     business day is one record in the source and one row here, with each
     measure in its own named, correctly-typed column — which is also the shape
     SOFR.csv has.

  3. CPI KEEPS ITS PERIOD CODE. CPI.csv is wide (Year x Jan..Dec, Annual,
     HALF1, HALF2). Stored long — one row per (year, period) — because a wide
     table cannot be charted or ranged over without unpivoting, and because the
     BLS API already returns it long. PeriodCode carries the publisher's own
     token (M01..M12, M13, S01, S02) and PeriodType says what the token means,
     so an annual average can never be averaged into a monthly trend.

  4. BI-TEMPORAL AND APPEND-ONLY, UNCHANGED. Both tables keep the two
     independent dates — the period the number describes, and when we learned
     it — and both keep every vintage (FR-4). This is the part of the previous
     design the data most clearly justifies: CPI.csv shows a missing October
     2025 that will be filled in later, and SOFR.csv carries a per-row revision
     indicator.

  5. core.SeriesCategory IS GONE. It existed to group series for drill-down.
     Two datasets in two tables need no grouping dimension.

  Requirement traceability
  ------------------------
  FR-2  collect.CollectionRun records every attempt, per source, with a failure
        category, so the scheduler logs failures instead of crashing.
  FR-3  Dedup: UQ_Cpi_Vintage / UQ_Sofr_Vintage, plus the collector only writes
        a new vintage when the value actually changed (RowHash).
  FR-4  Append-only. A revision adds a row; it never updates one.
  FR-5  Normalised: source -> run -> observation, one table per dataset.
  FR-6  Every stored row carries CollectedAtPkt alongside its reference date.
  FR-9  sec.AppUser / sec.Role / sec.UserRole back API authn/authz.
  NFR   Auditability: ai.AssistantSession.TranscriptJson records every question, the
        SQL it became, whether it passed validation and whether it was executed.
  NFR   Scalability: both fact tables are clustered on their date axis. At one
        CPI series and one rate the volumes are small by construction — roughly
        1,400 CPI rows for the full 1913-to-date history and ~250 SOFR rows a
        year — so partitioning is not modelled. See section 8.
==============================================================================*/

/*------------------------------------------------------------------------------
  0. Session options and schemas
  QUOTED_IDENTIFIER / ANSI_NULLS must be ON to create the filtered indexes
  below. Set here rather than left to the client: sqlcmd defaults
  QUOTED_IDENTIFIER to OFF.
------------------------------------------------------------------------------*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/*------------------------------------------------------------------------------
  TIME. Every ...AtPkt / ...ForPkt column holds a Pakistan Standard Time
  (UTC+05:00) wall-clock reading. datetime2 carries no offset, so the suffix is
  the only thing that says which clock a value is on -- keep it on any timestamp
  column added later.

  Defaults are DATEADD(hour, 5, SYSUTCDATETIME()) rather than
  SYSDATETIMEOFFSET() AT TIME ZONE 'Pakistan Standard Time': the AT TIME ZONE
  form needs a time-zone database entry whose name differs between Windows and
  Linux hosts, while Pakistan has observed no daylight saving since 2009, so the
  fixed offset is both the whole rule and the portable one. This mirrors
  PakistanTime in DataIntelligence.Core, which is where it changes if that ever
  stops being true.
------------------------------------------------------------------------------*/

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

    -- Both sources publish official JSON APIs, so the platform consumes those
    -- rather than scraping HTML (SOW 9: "prefer an official API if one exists").
    -- Csv is retained as a value because both publishers also offer the CSV
    -- download the sample extracts came from, which is the fallback importer.
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
    RobotsTxtCheckedAtPkt DATETIME2(3)  NULL,

    IsEnabled           BIT             NOT NULL CONSTRAINT DF_DataSource_Enabled DEFAULT 1,
    CreatedAtPkt        DATETIME2(3)    NOT NULL CONSTRAINT DF_DataSource_Created DEFAULT DATEADD(hour, 5, SYSUTCDATETIME()),
    UpdatedAtPkt        DATETIME2(3)    NULL,

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
    ScheduledForPkt     DATETIME2(0)    NOT NULL,
    Attempt             TINYINT         NOT NULL CONSTRAINT DF_CollectionRun_Attempt DEFAULT 1,
    TriggerType         VARCHAR(20)     NOT NULL CONSTRAINT DF_CollectionRun_Trigger DEFAULT 'Scheduled',

    StartedAtPkt        DATETIME2(3)    NOT NULL CONSTRAINT DF_CollectionRun_Started DEFAULT DATEADD(hour, 5, SYSUTCDATETIME()),
    CompletedAtPkt      DATETIME2(3)    NULL,
    DurationMs          AS DATEDIFF_BIG(MILLISECOND, StartedAtPkt, CompletedAtPkt),

    Status              VARCHAR(20)     NOT NULL CONSTRAINT DF_CollectionRun_Status DEFAULT 'Running',
    RequestUrl          NVARCHAR(1000)  NOT NULL,
    HttpStatusCode      SMALLINT        NULL,

    -- Counted in rows of the target table: a SOFR business day is one row, not
    -- six, now that its measures are columns rather than separate series.
    ObservationsFetched INT             NOT NULL CONSTRAINT DF_CollectionRun_Fetched  DEFAULT 0,
    ObservationsInserted INT            NOT NULL CONSTRAINT DF_CollectionRun_Inserted DEFAULT 0,
    ObservationsRevised INT             NOT NULL CONSTRAINT DF_CollectionRun_Revised  DEFAULT 0,
    ObservationsUnchanged INT           NOT NULL CONSTRAINT DF_CollectionRun_Unchanged DEFAULT 0,
    ObservationsRejected INT            NOT NULL CONSTRAINT DF_CollectionRun_Rejected DEFAULT 0,

    FailureCategory     VARCHAR(30)     NULL,
    ErrorMessage        NVARCHAR(1000)  NULL,
    ErrorDetail         NVARCHAR(MAX)   NULL,
    AlertSentAtPkt      DATETIME2(3)    NULL,   -- NFR Reliability: alert on failure

    CONSTRAINT PK_CollectionRun PRIMARY KEY (CollectionRunId),
    CONSTRAINT UQ_CollectionRun_Cycle UNIQUE (DataSourceId, ScheduledForPkt, Attempt),
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
        (CompletedAtPkt IS NULL OR CompletedAtPkt >= StartedAtPkt)
);
GO

CREATE INDEX IX_CollectionRun_StartedAtPkt
    ON collect.CollectionRun (StartedAtPkt DESC)
    INCLUDE (DataSourceId, Status, ObservationsInserted);

-- Failures are rare; a filtered index keeps the health and alerting queries cheap.
CREATE INDEX IX_CollectionRun_Failures
    ON collect.CollectionRun (StartedAtPkt DESC)
    INCLUDE (DataSourceId, FailureCategory, ErrorMessage, AlertSentAtPkt)
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
    FetchedAtPkt        DATETIME2(3)    NOT NULL CONSTRAINT DF_RawPayload_Fetched DEFAULT DATEADD(hour, 5, SYSUTCDATETIME()),
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
CREATE INDEX IX_RawPayload_Hash ON collect.RawPayload (ContentHash, FetchedAtPkt DESC);
GO

/*==============================================================================
  2. CURATED LAYER — one table per dataset
==============================================================================*/

/*------------------------------------------------------------------------------
  core.CpiObservation — BLS series CUUR0000SA0, and nothing else.

  Shape of the source (docs/example_data/CPI.csv, and the equivalent API rows):

      Year,Jan,Feb,...,Dec,Annual,HALF1,HALF2
      2025,317.671,319.082,...,324.054,321.943,320.229,324.000

  Stored long, one row per (year, period). PeriodCode is the publisher's own
  token, kept verbatim so a row can be traced back to the payload it came from:

      M01..M12  the twelve monthly index levels     -> PeriodType 'Month'
      M13       the CSV's "Annual" column           -> PeriodType 'Annual'
      S01/S02   the CSV's HALF1 / HALF2 columns     -> PeriodType 'Semiannual'

  Why the distinction is load-bearing: the annual and semiannual figures are
  averages OF the monthly ones. Anything that summed or averaged all rows of a
  year would count the same twelve numbers three times over. PeriodType makes
  that filterable, and every read model below filters on it explicitly.

  Two independent dates, both required:
    ReferenceDate  — first day of the period the number describes (2026-06-01
                     for M06; 2026-01-01 for M13 and S01; 2026-07-01 for S02).
                     The analytical axis.
    CollectedAtPkt — when this platform learned it (FR-6). The audit axis.
  Asking "what did we believe CPI for June was, on 15 July?" needs both.

  Gaps are absent, not zero. CPI.csv has no October 2025 value at the time of
  writing; the collector writes no row and the chart shows a gap, rather than
  inventing a level.
------------------------------------------------------------------------------*/
CREATE TABLE core.CpiObservation
(
    CpiObservationId    BIGINT          IDENTITY(1,1) NOT NULL,

    -- Pinned, not a foreign key: this table holds exactly one BLS series. The
    -- value is carried on the row so an extract is self-describing, and the
    -- CHECK is the single line to relax if a second series is ever commissioned
    -- (at which point it belongs in its own table, not mixed into this one).
    SeriesCode          VARCHAR(20)     NOT NULL CONSTRAINT DF_Cpi_SeriesCode DEFAULT 'CUUR0000SA0',

    ReferenceDate       DATE            NOT NULL,
    ReferenceYear       SMALLINT        NOT NULL,   -- the CSV's Year column
    PeriodCode          VARCHAR(3)      NOT NULL,   -- M01..M13, S01, S02 — verbatim
    PeriodType          VARCHAR(10)     NOT NULL,   -- Month | Annual | Semiannual

    -- The index level as published, base 1982-84 = 100. Three decimals is what
    -- BLS publishes from 2007 onward (earlier years carry one); values are never
    -- rescaled or rounded on the way in.
    IndexValue          DECIMAL(12,3)   NOT NULL,

    -- BLS footnote codes, verbatim and uninterpreted. "R" is the publisher
    -- saying the figure was revised, which is exactly what makes a new vintage.
    Footnotes           VARCHAR(100)    NULL,

    -- 0 is the first value we saw for this period; each later correction increments.
    RevisionNumber      SMALLINT        NOT NULL CONSTRAINT DF_Cpi_Revision DEFAULT 0,
    IsCurrent           BIT             NOT NULL CONSTRAINT DF_Cpi_Current DEFAULT 1,
    SupersededAtPkt     DATETIME2(3)    NULL,

    CollectionRunId     BIGINT          NOT NULL,
    CollectedAtPkt      DATETIME2(3)    NOT NULL,   -- FR-6

    -- SHA-256 over the value tuple, computed by the collector. Equal to the
    -- current vintage's hash means BLS reissued the same number, so no row is
    -- written (FR-3) — the common case when polling monthly data every hour.
    RowHash             BINARY(32)      NOT NULL,

    CONSTRAINT PK_CpiObservation PRIMARY KEY NONCLUSTERED (CpiObservationId),
    CONSTRAINT FK_CpiObservation_Run FOREIGN KEY (CollectionRunId)
        REFERENCES collect.CollectionRun (CollectionRunId),

    CONSTRAINT CK_Cpi_SeriesCode CHECK (SeriesCode = 'CUUR0000SA0'),
    -- Enumerated rather than a range: 'M1' sorts inside 'M01'..'M13' and would
    -- otherwise be accepted as a second, silent spelling of January.
    CONSTRAINT CK_Cpi_PeriodCode CHECK (PeriodCode IN
        ('M01','M02','M03','M04','M05','M06','M07','M08','M09','M10','M11','M12',
         'M13','S01','S02')),
    -- The token and its meaning must agree, or a filter on PeriodType silently
    -- lets an annual average into a monthly trend.
    CONSTRAINT CK_Cpi_PeriodType CHECK
        ((PeriodCode BETWEEN 'M01' AND 'M12' AND PeriodType = 'Month')
      OR (PeriodCode = 'M13'                 AND PeriodType = 'Annual')
      OR (PeriodCode IN ('S01','S02')        AND PeriodType = 'Semiannual')),
    -- ReferenceDate is derived from (year, period) by the collector; this is the
    -- assertion that the derivation was not skipped or mis-mapped.
    CONSTRAINT CK_Cpi_ReferenceDate CHECK
        (DAY(ReferenceDate) = 1
         AND YEAR(ReferenceDate) = ReferenceYear
         AND MONTH(ReferenceDate) =
                CASE WHEN PeriodCode BETWEEN 'M01' AND 'M12'
                          THEN CONVERT(INT, SUBSTRING(PeriodCode, 2, 2))
                     WHEN PeriodCode = 'S02' THEN 7
                     ELSE 1                       -- M13 and S01 both start in January
                END),
    CONSTRAINT CK_Cpi_ReferenceYear CHECK (ReferenceYear BETWEEN 1913 AND 2200),
    CONSTRAINT CK_Cpi_IndexValue CHECK (IndexValue > 0),
    CONSTRAINT CK_Cpi_Revision CHECK (RevisionNumber >= 0),
    -- A superseded row is not current, and a current row is not superseded.
    CONSTRAINT CK_Cpi_Superseded CHECK
        ((IsCurrent = 1 AND SupersededAtPkt IS NULL)
      OR (IsCurrent = 0 AND SupersededAtPkt IS NOT NULL))
);
GO

-- One row per period per vintage (FR-3), and the analytical ordering, because
-- those want the same key: every chart and trend query is a date range over this
-- one series. PeriodCode is in the key because M01, M13 and S01 all start on
-- 1 January, so the date alone does not identify a period. Page compression
-- because consecutive index levels share leading digits.
CREATE UNIQUE CLUSTERED INDEX UQ_Cpi_Vintage
    ON core.CpiObservation (ReferenceDate, PeriodCode, RevisionNumber)
    WITH (DATA_COMPRESSION = PAGE);

-- Exactly one current vintage per period. This is the integrity rule the
-- dashboards depend on: without it a botched revision could double-count a
-- month and no query would reveal it.
CREATE UNIQUE INDEX UQ_CpiObservation_Current
    ON core.CpiObservation (ReferenceYear, PeriodCode)
    WHERE IsCurrent = 1;

-- The monthly read: KPI tile, trend chart, and the collector's own
-- "what is the current value for this period?" dedup check.
CREATE INDEX IX_CpiObservation_Monthly
    ON core.CpiObservation (PeriodType, ReferenceDate DESC)
    INCLUDE (IndexValue, RowHash, IsCurrent, RevisionNumber);

CREATE INDEX IX_CpiObservation_Run ON core.CpiObservation (CollectionRunId);
GO

/*------------------------------------------------------------------------------
  core.SofrDailyRate — the Secured Overnight Financing Rate, one row per
  business day, rate type SOFR only.

  Shape of the source (docs/example_data/SOFR.csv):

      Effective Date,Rate Type,Rate (%),1st/25th/75th/99th Percentile (%),
      Volume ($Billions),Target Rate From/To (%),Intra Day Low/High (%),
      Standard Deviation (%),30/90/180-Day Average SOFR,SOFR Index,
      Revision Indicator (Y/N),Footnote ID

  The download carries five rates — EFFR, OBFR, TGCR, BGCR, SOFR — and only
  SOFR is in scope, so RateType is pinned and the other four are rejected into
  core.RejectedObservation rather than stored. Target Rate From/To belongs to
  EFFR (it is the FOMC target band) and is empty on every SOFR row, so it has no
  column here; Intra Day Low/High and Standard Deviation are likewise empty for
  SOFR in the published file.

  The six measures that ARE populated — rate, four percentiles, volume — are
  columns rather than rows. They are one observation of one instrument on one
  day, and splitting them across rows would mean five self-joins to draw the
  distribution band that is the whole point of publishing them.

  The averages and the index are modelled but not collected. The NY Fed
  publishes them on a separate SOFR Averages and Index endpoint rather than on
  the daily rate record, and they are empty in the CSV extract too. The columns
  exist so adding that endpoint later is an adapter change rather than a
  migration — and so nothing is ever tempted to compute a "SOFR average" here
  and store it where the publisher's own compounded figure belongs.
------------------------------------------------------------------------------*/
CREATE TABLE core.SofrDailyRate
(
    SofrDailyRateId     BIGINT          IDENTITY(1,1) NOT NULL,

    -- Pinned for the same reason as CPI's SeriesCode: this table is one rate.
    RateType            VARCHAR(5)      NOT NULL CONSTRAINT DF_Sofr_RateType DEFAULT 'SOFR',

    -- The business day the rate covers — the CSV's "Effective Date". Published
    -- the following business morning, ~08:00 ET, which is why this and
    -- CollectedAtPkt are never the same instant.
    EffectiveDate       DATE            NOT NULL,

    RatePercent         DECIMAL(9,5)    NOT NULL,   -- "Rate (%)", percent per annum
    Percentile1Percent  DECIMAL(9,5)    NULL,
    Percentile25Percent DECIMAL(9,5)    NULL,
    Percentile75Percent DECIMAL(9,5)    NULL,
    Percentile99Percent DECIMAL(9,5)    NULL,

    -- "Volume ($Billions)", stored in the unit published. Never rescaled to
    -- dollars on the way in: a silent factor of a billion is not visible in the
    -- data afterwards.
    VolumeUsdBillions   DECIMAL(12,3)   NULL,

    -- Publisher-computed compounded averages and the index. Empty in the CSV
    -- extract, populated by the API.
    Average30DayPercent DECIMAL(9,5)    NULL,
    Average90DayPercent DECIMAL(9,5)    NULL,
    Average180DayPercent DECIMAL(9,5)   NULL,
    SofrIndexValue      DECIMAL(20,8)   NULL,       -- published to 8 decimals

    -- "Revision Indicator (Y/N)" and "Footnote ID", verbatim. The indicator is
    -- the publisher's statement about the row; RevisionNumber below is this
    -- platform's count of how many times we have seen it change. They are not
    -- the same thing and both are kept.
    RevisionIndicator   CHAR(1)         NULL,
    FootnoteId          VARCHAR(20)     NULL,

    RevisionNumber      SMALLINT        NOT NULL CONSTRAINT DF_Sofr_Revision DEFAULT 0,
    IsCurrent           BIT             NOT NULL CONSTRAINT DF_Sofr_Current DEFAULT 1,
    SupersededAtPkt     DATETIME2(3)    NULL,

    CollectionRunId     BIGINT          NOT NULL,
    CollectedAtPkt      DATETIME2(3)    NOT NULL,   -- FR-6

    RowHash             BINARY(32)      NOT NULL,   -- FR-3, as for CPI

    CONSTRAINT PK_SofrDailyRate PRIMARY KEY NONCLUSTERED (SofrDailyRateId),
    CONSTRAINT FK_SofrDailyRate_Run FOREIGN KEY (CollectionRunId)
        REFERENCES collect.CollectionRun (CollectionRunId),

    CONSTRAINT CK_Sofr_RateType CHECK (RateType = 'SOFR'),
    CONSTRAINT CK_Sofr_RevisionIndicator CHECK
        (RevisionIndicator IS NULL OR RevisionIndicator IN ('Y','N')),
    -- A decimal-shift parse bug produces 365 or 0.0365 where 3.65 was meant.
    -- The band is deliberately far wider than any rate the Fed has ever set, so
    -- it catches the bug without ever having an opinion on monetary policy.
    CONSTRAINT CK_Sofr_RateRange CHECK (RatePercent BETWEEN -5 AND 25),
    CONSTRAINT CK_Sofr_Volume CHECK (VolumeUsdBillions IS NULL OR VolumeUsdBillions >= 0),
    -- Percentiles are ordered by definition. If they arrive out of order the
    -- columns have been mapped to the wrong fields.
    CONSTRAINT CK_Sofr_PercentileOrder CHECK
        ((Percentile1Percent  IS NULL OR Percentile25Percent IS NULL
            OR Percentile1Percent  <= Percentile25Percent)
     AND (Percentile25Percent IS NULL OR Percentile75Percent IS NULL
            OR Percentile25Percent <= Percentile75Percent)
     AND (Percentile75Percent IS NULL OR Percentile99Percent IS NULL
            OR Percentile75Percent <= Percentile99Percent)),
    CONSTRAINT CK_Sofr_Revision CHECK (RevisionNumber >= 0),
    CONSTRAINT CK_Sofr_Superseded CHECK
        ((IsCurrent = 1 AND SupersededAtPkt IS NULL)
      OR (IsCurrent = 0 AND SupersededAtPkt IS NOT NULL))
);
GO

-- As for CPI: the vintage key and the analytical ordering are the same key, so
-- one clustered unique index serves both.
CREATE UNIQUE CLUSTERED INDEX UQ_Sofr_Vintage
    ON core.SofrDailyRate (EffectiveDate, RevisionNumber)
    WITH (DATA_COMPRESSION = PAGE);

-- Exactly one current vintage per business day.
CREATE UNIQUE INDEX UQ_SofrDailyRate_Current
    ON core.SofrDailyRate (EffectiveDate)
    WHERE IsCurrent = 1;

CREATE INDEX IX_SofrDailyRate_Run ON core.SofrDailyRate (CollectionRunId);
GO

/*------------------------------------------------------------------------------
  core.RejectedObservation — parsed but unusable, or out of scope. Keeps bad
  data out of the fact tables while preserving the evidence; a rejection spike
  is the earliest signal that a publisher changed its payload shape.

  Note that the out-of-scope rates (EFFR, OBFR, TGCR, BGCR) are an expected,
  steady stream of 'UnknownSeries' rejections rather than an anomaly — four per
  business day. Anything else appearing here is worth looking at.
------------------------------------------------------------------------------*/
CREATE TABLE core.RejectedObservation
(
    RejectedObservationId BIGINT        IDENTITY(1,1) NOT NULL,
    CollectionRunId     BIGINT          NOT NULL,
    -- The BLS series id, or the NY Fed rate type, as published.
    SeriesCode          NVARCHAR(100)   NULL,
    ReferenceDateText   NVARCHAR(50)    NULL,   -- as published; may be why it was rejected
    RejectedAtPkt       DATETIME2(3)    NOT NULL CONSTRAINT DF_Rejected_At DEFAULT DATEADD(hour, 5, SYSUTCDATETIME()),
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
    ON core.RejectedObservation (CollectionRunId, RejectedAtPkt DESC);
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
    CreatedAtPkt        DATETIME2(3)    NOT NULL CONSTRAINT DF_AppUser_Created DEFAULT DATEADD(hour, 5, SYSUTCDATETIME()),
    LastLoginAtPkt      DATETIME2(3)    NULL,

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
    GrantedAtPkt        DATETIME2(3)    NOT NULL CONSTRAINT DF_UserRole_Granted DEFAULT DATEADD(hour, 5, SYSUTCDATETIME()),

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

/*------------------------------------------------------------------------------
  The placeholder the assistant runs as until FR-9 lands.

  Not optional, and not a convenience. Authentication is not built yet, so
  AssistantEndpoints passes a hard-coded userId: 1 with every question, and both
  ai.AssistantSession and ai.AssistantQuery carry a foreign key to sec.AppUser.
  Without this row the FIRST question anyone asks fails on
  FK_AssistantSession_User -- a fresh database that looks perfectly healthy until
  someone opens the assistant.

  UserId is IDENTITY, so the value is forced rather than allocated: the endpoint
  names 1 specifically, and a row that happened to land on 2 would not satisfy it.
  Identity resumes at 2 afterwards, leaving real accounts unaffected.

  PasswordHash is deliberately not a hash. ASP.NET Identity v3 stores base64 of a
  byte array; this string cannot decode as one, so no password can ever verify
  against it and the placeholder cannot become a way in. FR-9 should delete this
  row once real accounts exist -- reassign or remove the audit rows pointing at
  it first, since those are the record of who asked what.
------------------------------------------------------------------------------*/
SET IDENTITY_INSERT sec.AppUser ON;

IF NOT EXISTS (SELECT 1 FROM sec.AppUser WHERE UserId = 1)
    INSERT INTO sec.AppUser (UserId, Email, DisplayName, PasswordHash, IsActive) VALUES
        (1, N'assistant-placeholder@local', N'Placeholder (pre-FR-9)', N'!NO-LOGIN!', 1);

SET IDENTITY_INSERT sec.AppUser OFF;
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
    StartedAtPkt        DATETIME2(3)    NOT NULL CONSTRAINT DF_Session_Started DEFAULT DATEADD(hour, 5, SYSUTCDATETIME()),
    LastActivityAtPkt   DATETIME2(3)    NOT NULL CONSTRAINT DF_Session_Activity DEFAULT DATEADD(hour, 5, SYSUTCDATETIME()),

    -- The whole conversation as one JSON document: every turn, in order, as the chat was had.
    -- THIS IS THE RECORD. ai.AssistantQuery is no longer written; what is not in here was not
    -- kept. Each turn carries the question, the SQL it became, the bound parameters, the outcome,
    -- the answer, which model produced it, token usage and timings -- see ChatTranscriptTurn, which
    -- defines the shape, and section 5's note on the audit-log view that reads it back with
    -- OPENJSON.
    --
    -- The model is recorded twice per turn, as modelChoice ('Cloud' or 'Local') and modelName (the
    -- id its gateway spells, 'deepseek-v4-flash' or 'qwen3.5:2b'), because the two answer different
    -- questions: which model served the turn, and which of the two options the user picked. Point
    -- Assistant:Local:Model at something else and every turn written before it keeps its own name
    -- and still reads as Local. Both are NULL on turns written before the model became a choice --
    -- a document store has no migration step, so an older turn simply lacks the field and OPENJSON
    -- returns NULL. That is the honest reading and is not backfilled: those turns reached the only
    -- gateway there was, but nothing here says so.
    --
    -- Result rows are excluded: a turn may return up to 2,000 of them, and a document rewritten on
    -- every turn would grow by megabytes to hold what re-running the stored statement reproduces.
    --
    -- Two costs of keeping a conversation as a document rather than as rows. Writes are
    -- read-modify-write of the whole thing, so two questions answered against one session at the
    -- same instant will both load it and the later save will drop the other's turn -- a row insert
    -- could not lose a turn. And nothing here can be indexed: the review queue shreds every
    -- transcript in the table on every call, where it used to seek IX_AssistantQuery_Rejected.
    TranscriptJson      NVARCHAR(MAX)   NULL,

    -- What the whole conversation has cost the model, in tokens: the turns' own totals added up.
    -- Derived from TranscriptJson and therefore a denormalisation, kept because the chat list shows
    -- this for every conversation a user has -- computing it there would shred each of their
    -- transcripts with OPENJSON to return one integer per chat, which is the entire history parsed
    -- to draw a list. AssistantService rewrites it on every save, and is the only writer; anything
    -- that edits TranscriptJson without going through it leaves this stale and nothing here will
    -- notice.
    --
    -- NULL means no turn in the conversation reported usage -- not known, as opposed to a chat that
    -- cost nothing, which is why it is nullable rather than DEFAULT 0. Where only some turns
    -- reported usage this is a floor rather than an exact figure.
    TotalTokens         INT             NULL,

    CONSTRAINT PK_AssistantSession PRIMARY KEY (SessionId),
    CONSTRAINT FK_AssistantSession_User FOREIGN KEY (UserId) REFERENCES sec.AppUser (UserId),
    -- Checked by the database, not trusted from the application. A malformed document is otherwise
    -- found by whoever reads the transcript back, which can be long after the write that broke it.
    CONSTRAINT CK_AssistantSession_TranscriptJson CHECK
        (TranscriptJson IS NULL OR ISJSON(TranscriptJson) = 1)
);
GO

CREATE INDEX IX_AssistantSession_User ON ai.AssistantSession (UserId, StartedAtPkt DESC);
GO

CREATE TABLE ai.AssistantFeedback
(
    AssistantQueryId    BIGINT          NOT NULL,
    IsHelpful           BIT             NOT NULL,
    Comment             NVARCHAR(1000)  NULL,
    SubmittedAtPkt      DATETIME2(3)    NOT NULL CONSTRAINT DF_Feedback_At DEFAULT DATEADD(hour, 5, SYSUTCDATETIME()),

    -- No foreign key. The turn this feedback is about lives inside
    -- ai.AssistantSession.TranscriptJson, and a key cannot reference a value inside a document.
    -- AssistantQueryId is therefore an unenforced id drawn from ai.AssistantTurnId: nothing stops
    -- a row here from naming a turn that does not exist, and deleting a session no longer cascades
    -- to the feedback on its turns. The API checks the turn exists before writing, which covers
    -- everything going through it and nothing going around it.
    CONSTRAINT PK_AssistantFeedback PRIMARY KEY (AssistantQueryId)
);
GO

/*------------------------------------------------------------------------------
  Turn ids.

  ai.AssistantQuery used to allocate these from its IDENTITY column. Turns now
  live in ai.AssistantSession.TranscriptJson and no row is inserted per turn, so
  the id has to come from somewhere that is not a table.

  It must be unique across sessions, not within one: this is the id the API
  returns and the id POST /assistant/queries/{id}/feedback takes, so numbering
  turns 1..n per conversation would have every session claiming turn 1.

  START WITH 1 is right only for a new database. An existing one that has already
  written ai.AssistantQuery rows must start above the last of them, or new turns
  will reuse ids that old feedback rows still point at:
      DECLARE @next BIGINT = (SELECT ISNULL(MAX(AssistantQueryId), 0) + 1 FROM ai.AssistantQuery);
      DECLARE @sql NVARCHAR(200) = N'ALTER SEQUENCE ai.AssistantTurnId RESTART WITH ' + CAST(@next AS NVARCHAR(20));
      EXEC sp_executesql @sql;
------------------------------------------------------------------------------*/
CREATE SEQUENCE ai.AssistantTurnId AS BIGINT START WITH 1 INCREMENT BY 1 NO CACHE;
GO

/*==============================================================================
  5. READ MODELS
  The AI assistant is granted SELECT on these views only (least privilege). It
  never sees sec.* or ai.*, which removes a class of prompt-injection data
  exposure and shrinks the schema the model has to reason about.

  Every view filters to IsCurrent = 1, so an ordinary question gets the current
  vintage. Revision history stays reachable in the base tables for anyone who
  needs it, which is the right default: "what is CPI for June" should not return
  three different answers.
==============================================================================*/

/*------------------------------------------------------------------------------
  Current-vintage CPI, every period type, with the period spelled out. The
  labels exist so a natural-language question about "the 2025 annual figure"
  has something to match on besides the token 'M13'.
------------------------------------------------------------------------------*/
CREATE VIEW analytics.vw_Cpi
AS
SELECT  c.ReferenceDate,
        c.ReferenceYear,
        c.PeriodCode,
        c.PeriodType,
        CASE c.PeriodCode
             WHEN 'M13' THEN N'Annual average'
             WHEN 'S01' THEN N'First half'
             WHEN 'S02' THEN N'Second half'
             ELSE DATENAME(month, c.ReferenceDate)
        END                     AS PeriodLabel,
        c.SeriesCode,
        N'CPI-U, all items, U.S. city average, not seasonally adjusted' AS SeriesTitle,
        N'Index 1982-84=100'    AS Unit,
        c.IndexValue,
        c.RevisionNumber,
        c.Footnotes,
        c.CollectedAtPkt
FROM    core.CpiObservation AS c
WHERE   c.IsCurrent = 1;
GO

/*------------------------------------------------------------------------------
  Monthly CPI with month-over-month and year-over-year change — the headline CPI
  number is year-over-year inflation, not the index level, so the platform
  computes it rather than making every caller rediscover it.

  Restricted to PeriodType = 'Month' at every level, and joined on an explicit
  date offset rather than LAG(): a positional lag would happily compare June
  against the annual average, or skip over the missing month the source has.
------------------------------------------------------------------------------*/
CREATE VIEW analytics.vw_CpiMonthlyChange
AS
SELECT  cur.ReferenceDate,
        cur.ReferenceYear,
        cur.PeriodCode,
        cur.IndexValue,
        prev.IndexValue         AS PreviousMonthValue,
        CASE WHEN prev.IndexValue IS NULL OR prev.IndexValue = 0 THEN NULL
             ELSE CONVERT(DECIMAL(18,6),
                    (cur.IndexValue - prev.IndexValue) * 100.0 / prev.IndexValue)
        END                     AS MonthOverMonthPct,
        yago.IndexValue         AS YearAgoValue,
        CASE WHEN yago.IndexValue IS NULL OR yago.IndexValue = 0 THEN NULL
             ELSE CONVERT(DECIMAL(18,6),
                    (cur.IndexValue - yago.IndexValue) * 100.0 / yago.IndexValue)
        END                     AS YearOverYearPct   -- the headline inflation rate
FROM    core.CpiObservation AS cur
LEFT JOIN core.CpiObservation AS prev
       ON prev.IsCurrent = 1
      AND prev.PeriodType = 'Month'
      AND prev.ReferenceDate = DATEADD(month, -1, cur.ReferenceDate)
LEFT JOIN core.CpiObservation AS yago
       ON yago.IsCurrent = 1
      AND yago.PeriodType = 'Month'
      AND yago.ReferenceDate = DATEADD(year, -1, cur.ReferenceDate)
WHERE   cur.IsCurrent = 1
  AND   cur.PeriodType = 'Month';
GO

/*------------------------------------------------------------------------------
  The annual and semiannual figures BLS publishes, pivoted back to one row per
  year — the Annual / HALF1 / HALF2 columns of CPI.csv, reassembled. These are
  the publisher's own averages, not ours: nothing here recomputes them from the
  monthly rows.
------------------------------------------------------------------------------*/
CREATE VIEW analytics.vw_CpiAnnual
AS
SELECT  c.ReferenceYear,
        MAX(CASE WHEN c.PeriodCode = 'M13' THEN c.IndexValue END) AS AnnualValue,
        MAX(CASE WHEN c.PeriodCode = 'S01' THEN c.IndexValue END) AS FirstHalfValue,
        MAX(CASE WHEN c.PeriodCode = 'S02' THEN c.IndexValue END) AS SecondHalfValue,
        MAX(c.CollectedAtPkt)                                     AS CollectedAtPkt
FROM    core.CpiObservation AS c
WHERE   c.IsCurrent = 1
  AND   c.PeriodType IN ('Annual','Semiannual')
GROUP BY c.ReferenceYear;
GO

-- Current-vintage SOFR: the daily rate with its distribution and volume.
CREATE VIEW analytics.vw_Sofr
AS
SELECT  s.EffectiveDate,
        s.RateType,
        N'Secured Overnight Financing Rate' AS SeriesTitle,
        s.RatePercent,
        s.Percentile1Percent,
        s.Percentile25Percent,
        s.Percentile75Percent,
        s.Percentile99Percent,
        s.VolumeUsdBillions,
        s.Average30DayPercent,
        s.Average90DayPercent,
        s.Average180DayPercent,
        s.SofrIndexValue,
        s.RevisionIndicator,
        s.RevisionNumber,
        s.CollectedAtPkt
FROM    core.SofrDailyRate AS s
WHERE   s.IsCurrent = 1;
GO

/*------------------------------------------------------------------------------
  SOFR summarised per calendar year — the "annual SOFR" figure, and the view the
  current year's dashboard reads.

  AverageRatePercent is the simple mean of the published daily rates over the
  days present. It is NOT the NY Fed's compounded 30/90/180-day average or the
  SOFR Index: those are the publisher's own, carry their own compounding
  convention, and are stored verbatim in the columns of the same name. Anyone
  needing a compounded figure should use theirs, not this.
------------------------------------------------------------------------------*/
CREATE VIEW analytics.vw_SofrAnnual
AS
SELECT  YEAR(s.EffectiveDate)                       AS CalendarYear,
        COUNT_BIG(*)                                AS BusinessDays,
        MIN(s.EffectiveDate)                        AS FirstEffectiveDate,
        MAX(s.EffectiveDate)                        AS LastEffectiveDate,
        CONVERT(DECIMAL(9,5), AVG(s.RatePercent))   AS AverageRatePercent,
        MIN(s.RatePercent)                          AS MinRatePercent,
        MAX(s.RatePercent)                          AS MaxRatePercent,
        CONVERT(DECIMAL(18,3), AVG(s.VolumeUsdBillions)) AS AverageVolumeUsdBillions,
        CONVERT(DECIMAL(18,3), SUM(s.VolumeUsdBillions)) AS TotalVolumeUsdBillions
FROM    core.SofrDailyRate AS s
WHERE   s.IsCurrent = 1
GROUP BY YEAR(s.EffectiveDate);
GO

/*------------------------------------------------------------------------------
  The KPI tiles (FR-10): the latest figure from each dataset, side by side.
  Two rows, always — the dashboard header does not need to know that one number
  comes from a monthly index and the other from a daily rate.
------------------------------------------------------------------------------*/
CREATE VIEW analytics.vw_LatestIndicator
AS
SELECT  'CPI'                   AS IndicatorCode,
        N'CPI-U, all items, U.S. city average (NSA)' AS IndicatorName,
        N'Index 1982-84=100'    AS Unit,
        c.ReferenceDate         AS AsOfDate,
        c.IndexValue            AS Value,
        c.CollectedAtPkt
FROM   (SELECT TOP (1) o.ReferenceDate, o.IndexValue, o.CollectedAtPkt
        FROM   core.CpiObservation AS o
        WHERE  o.IsCurrent = 1
          AND  o.PeriodType = 'Month'
        ORDER  BY o.ReferenceDate DESC) AS c
UNION ALL
SELECT  'SOFR',
        N'Secured Overnight Financing Rate',
        N'Percent per annum',
        s.EffectiveDate,
        s.RatePercent,
        s.CollectedAtPkt
FROM   (SELECT TOP (1) r.EffectiveDate, r.RatePercent, r.CollectedAtPkt
        FROM   core.SofrDailyRate AS r
        WHERE  r.IsCurrent = 1
        ORDER  BY r.EffectiveDate DESC) AS s;
GO

/*------------------------------------------------------------------------------
  Revision history: how a published figure moved after first release. Only
  periods that have actually been revised appear, so an empty result is the
  honest answer to "has anything been restated?".
------------------------------------------------------------------------------*/
CREATE VIEW analytics.vw_CpiRevision
AS
SELECT  c.ReferenceDate,
        c.ReferenceYear,
        c.PeriodCode,
        c.RevisionNumber,
        c.IndexValue,
        c.IsCurrent,
        c.Footnotes,
        c.CollectedAtPkt,
        c.SupersededAtPkt
FROM    core.CpiObservation AS c
WHERE   EXISTS (SELECT 1 FROM core.CpiObservation AS r
                WHERE r.ReferenceYear = c.ReferenceYear
                  AND r.PeriodCode = c.PeriodCode
                  AND r.RevisionNumber > 0);
GO

CREATE VIEW analytics.vw_SofrRevision
AS
SELECT  s.EffectiveDate,
        s.RevisionNumber,
        s.RatePercent,
        s.VolumeUsdBillions,
        s.IsCurrent,
        s.RevisionIndicator,
        s.CollectedAtPkt,
        s.SupersededAtPkt
FROM    core.SofrDailyRate AS s
WHERE   EXISTS (SELECT 1 FROM core.SofrDailyRate AS r
                WHERE r.EffectiveDate = s.EffectiveDate
                  AND r.RevisionNumber > 0);
GO

-- Collection health per source: the >=99% / rolling-30-day reliability NFR.
CREATE VIEW analytics.vw_CollectionHealth
AS
SELECT  d.Code                                                          AS SourceCode,
        d.Name                                                          AS SourceName,
        CONVERT(date, r.StartedAtPkt)                                   AS RunDate,
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
-- PKT on both sides. StartedAtPkt is a Pakistan wall-clock reading, so comparing it
-- against SYSUTCDATETIME() would slide the 30-day window by five hours -- small, silent,
-- and wrong in the direction of hiding the most recent runs.
WHERE   r.StartedAtPkt >= DATEADD(day, -30, DATEADD(hour, 5, SYSUTCDATETIME()))
  AND   r.Status <> 'Running'
GROUP BY d.Code, d.Name, CONVERT(date, r.StartedAtPkt);
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

GRANT SELECT ON analytics.vw_Cpi                TO di_ai_readonly;
GRANT SELECT ON analytics.vw_CpiMonthlyChange   TO di_ai_readonly;
GRANT SELECT ON analytics.vw_CpiAnnual          TO di_ai_readonly;
GRANT SELECT ON analytics.vw_Sofr               TO di_ai_readonly;
GRANT SELECT ON analytics.vw_SofrAnnual         TO di_ai_readonly;
GRANT SELECT ON analytics.vw_LatestIndicator    TO di_ai_readonly;
GRANT SELECT ON analytics.vw_CpiRevision        TO di_ai_readonly;
GRANT SELECT ON analytics.vw_SofrRevision       TO di_ai_readonly;
GRANT SELECT ON analytics.vw_CollectionHealth   TO di_ai_readonly;
DENY  SELECT ON SCHEMA::sec TO di_ai_readonly;
DENY  SELECT ON SCHEMA::ai  TO di_ai_readonly;
GO

/*------------------------------------------------------------------------------
  A principal the API can actually reach the role through.

  di_ai_readonly is a role, and a role cannot be connected as. Where the server
  runs mixed-mode authentication, create a login, add it to the role, and point
  ConnectionStrings:DataIntelligenceDbReadOnly at it -- that is the stronger
  arrangement, because the restricted rights are carried by the connection.

  On a Windows-authentication-only instance there is no login to add, so the
  API keeps its own connection and switches session context for the duration of
  each generated statement (Assistant:ExecuteAsUser). A user WITHOUT LOGIN is
  exactly the principal for that: it cannot be logged into, and can only be
  reached by EXECUTE AS from a session that is already authenticated.

  Either way the statement ends up running as a principal that holds SELECT on
  analytics.* and DENY on sec and ai.
------------------------------------------------------------------------------*/
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'di_ai_user')
BEGIN
    CREATE USER di_ai_user WITHOUT LOGIN;
    ALTER ROLE di_ai_readonly ADD MEMBER di_ai_user;
END
GO

-- The app's own login must be allowed to become it, or EXECUTE AS is refused.
GRANT IMPERSONATE ON USER::di_ai_user TO di_app;
GO

/*==============================================================================
  7. SEED — the designated sources (SOW 0.1)
------------------------------------------------------------------------------
  These are reference data, not user configuration: the platform is commissioned
  against these two publishers. Seeding them here means DataSourceId is stable
  across environments, so configuration and logs can refer to a source by Code.

  There is no series seed any more. Each dataset is a table, so "which series do
  we collect" is answered by the schema itself rather than by rows that could be
  edited into disagreement with the collector.
==============================================================================*/
INSERT INTO collect.DataSource
    (DataSourceId, Code, Name, Publisher, LandingPageUrl, ApiEndpoint,
     AccessMethod, HttpMethod, RequiresApiKey, PublicationCadence,
     CollectionIntervalMinutes, TermsOfUseUrl, IsEnabled)
VALUES
    -- CPI. The POST body names the one series in scope, CUUR0000SA0, and the
    -- year range; the endpoint itself carries neither, which is why the series
    -- id appears in core.CpiObservation.SeriesCode and not here.
    (1, 'BLS_CPI', N'US Consumer Price Index (CUUR0000SA0)', N'U.S. Bureau of Labor Statistics',
     N'https://www.bls.gov/data/home.htm',
     N'https://api.bls.gov/publicAPI/v2/timeseries/data/',
     'RestApi', 'POST',
     -- Unregistered v2 calls work but are rate-limited; a registered key raises the
     -- daily budget. Flagged so the sponsor can decide whether to register.
     0, 'Monthly', 60,
     N'https://www.bls.gov/developers/api_faqs.htm', 1),

    -- SOFR. The search endpoint takes a date range, and the collector asks for
    -- the current calendar year every cycle — 1 January to today. That is the
    -- "annual" extract in docs/example_data/SOFR.csv, and requesting the whole
    -- year rather than the last few days means a gap from an outage or a late
    -- revision is repaired by the next cycle instead of persisting.
    (2, 'NYFED_SOFR', N'Secured Overnight Financing Rate', N'Federal Reserve Bank of New York',
     N'https://www.newyorkfed.org/markets/reference-rates/sofr',
     N'https://markets.newyorkfed.org/api/rates/secured/sofr/search.json?startDate={yyyy-01-01}&endDate={today}',
     'RestApi', 'GET', 0, 'BusinessDaily', 60,
     N'https://www.newyorkfed.org/markets/reference-rates/terms-of-use-for-selected-rate-data', 1);
GO

/*==============================================================================
  8. NOTES FOR PHASE 4
------------------------------------------------------------------------------
  Revision handling. Inserting a revision is two statements in one transaction:
  clear IsCurrent (setting SupersededAtPkt) on the existing current row, then
  insert the new vintage with RevisionNumber + 1. UQ_CpiObservation_Current and
  UQ_SofrDailyRate_Current make a mistake here a hard failure rather than a
  silently duplicated period.

  Out-of-scope rates. The NY Fed payload carries EFFR, OBFR, TGCR and BGCR
  alongside SOFR. They are filtered in the adapter and logged as 'UnknownSeries'
  rejections, so the exclusion is visible in the data rather than invisible in
  the code. If one is later commissioned it gets its own table, on the same
  pattern — not a RateType value in this one, which is what CK_Sofr_RateType is
  there to prevent by accident.

  Backfill. CPI.csv reaches back to 1913. The BLS API caps a single request at
  20 years, so a full-history backfill is several requests with TriggerType =
  'Backfill'; the day-to-day cycle asks only for the current and previous year,
  which is what the year-over-year comparison and the annual revision need.

  Polling cadence versus publication cadence. FR-1 specifies hourly collection.
  CPI is monthly and SOFR is business-daily, so the overwhelming majority of
  cycles will find nothing new. That is handled, not wasted: the collector
  compares RowHash and records the run as succeeded with zero inserts, and
  collect.RawPayload.ContentHash short-circuits an unchanged body before parsing.
  Worth confirming with the sponsor whether hourly is still wanted, or whether
  polling aligned to publication (SOFR ~08:00 ET; CPI on the BLS release
  calendar) is a better fit — see the Scope Document refinement.

  Partitioning (NFR Scalability). Not applied, and no longer designed for. One
  CPI series is ~1,400 rows for the entire published history since 1913, and
  SOFR is ~250 rows a year. Both tables are clustered on their date axis, which
  is what the range queries actually need; if volumes ever justified partitions
  the clustering key already leads with the partition column.

  Retention. collect.RawPayload is diagnostic and is the bulk of the storage;
  purge beyond ~90 days. The two fact tables are never purged (FR-4).

  Append-only enforcement. Both fact tables are append-only by convention,
  except for the IsCurrent/SupersededAtPkt flip that a revision performs. If the
  sponsor wants it enforced in the database, an INSTEAD OF DELETE trigger plus
  an UPDATE trigger restricted to those two columns is the lighter option.

  EF Core. This script is the design of record; the equivalent is generated as a
  migration (20260805122858_InitialTwoTableSchema) so the migration history
  remains the deployment mechanism (NFR Maintainability). The two are verified
  identical by creating a database from each and diffing columns, indexes and
  constraints from sys.*: every index, filter and CHECK matches exactly.

  The one systematic difference is cosmetic and unavoidable. EF emits a scalar
  default as CONVERT([tinyint],(1)) where this script writes 1, so sys.columns
  reports DEFAULT (CONVERT([tinyint],(1))) against DEFAULT ((1)). Same type, same
  value, same behaviour — chasing it would mean writing CONVERT() casts through
  a script whose readability is the reason it exists.
==============================================================================*/
