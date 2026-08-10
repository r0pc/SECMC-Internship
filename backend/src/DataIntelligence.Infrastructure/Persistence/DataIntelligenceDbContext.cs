using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataIntelligence.Infrastructure.Persistence;

/// <summary>
/// EF Core model for the collection and curated layers. Mirrors
/// <c>docs/database-schema.sql</c>, which remains the design of record; migrations generated
/// from this context are the deployment mechanism (NFR Maintainability).
/// </summary>
/// <remarks>
/// The <c>sec</c> schema is not mapped here — it belongs to the authentication work (FR-9) and is
/// untouched by collection. The <c>ai</c> schema is mapped, because the assistant's audit log is
/// written through this context (NFR Auditability). Its <c>UserId</c> columns are plain integers
/// rather than mapped relationships: <c>docs/database-schema.sql</c> gives them a foreign key to
/// <c>sec.AppUser</c>, and that constraint can only be added once FR-9 creates the table it points
/// at. Recorded in the migration as a TODO rather than silently dropped.
/// </remarks>
public class DataIntelligenceDbContext : DbContext
{
    public DataIntelligenceDbContext(DbContextOptions<DataIntelligenceDbContext> options)
        : base(options)
    {
    }

    public DbSet<DataSource> DataSources => Set<DataSource>();
    public DbSet<CollectionRun> CollectionRuns => Set<CollectionRun>();
    public DbSet<RawPayload> RawPayloads => Set<RawPayload>();
    public DbSet<CpiObservation> CpiObservations => Set<CpiObservation>();
    public DbSet<SofrDailyRate> SofrDailyRates => Set<SofrDailyRate>();
    public DbSet<RejectedObservation> RejectedObservations => Set<RejectedObservation>();
    public DbSet<AssistantSession> AssistantSessions => Set<AssistantSession>();
    public DbSet<AssistantQuery> AssistantQueries => Set<AssistantQuery>();
    public DbSet<AssistantFeedback> AssistantFeedback => Set<AssistantFeedback>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Millisecond precision for every timestamp. EF's default is datetime2(7), which costs
        // two bytes a row for sub-millisecond resolution no publisher provides. Columns needing
        // something else (ScheduledForUtc) set it explicitly.
        configurationBuilder.Properties<DateTime>().HavePrecision(3);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureDataSource(modelBuilder.Entity<DataSource>());
        ConfigureCollectionRun(modelBuilder.Entity<CollectionRun>());
        ConfigureRawPayload(modelBuilder.Entity<RawPayload>());
        ConfigureCpiObservation(modelBuilder.Entity<CpiObservation>());
        ConfigureSofrDailyRate(modelBuilder.Entity<SofrDailyRate>());
        ConfigureRejectedObservation(modelBuilder.Entity<RejectedObservation>());
        ConfigureAssistantSession(modelBuilder.Entity<AssistantSession>());
        ConfigureAssistantQuery(modelBuilder.Entity<AssistantQuery>());
        ConfigureAssistantFeedback(modelBuilder.Entity<AssistantFeedback>());

        ConfigureSeedData(modelBuilder);
    }

    /// <summary>
    /// The designated sources (SOW 0.1), seeded through the migration so a fresh deployment has
    /// something to collect from.
    /// </summary>
    /// <remarks>
    /// Reference data rather than user configuration: the platform is commissioned against these
    /// two publishers, and seeding here keeps identifiers stable across environments so config
    /// and logs can refer to a source by code. Mirrors section 7 of the hand-written schema.
    /// <para>
    /// There is no series seed. Each dataset is a table, so "which series do we collect" is
    /// answered by the schema rather than by rows that could be edited into disagreement with the
    /// collector; what a chart may draw is <c>SeriesCatalog</c>, in code.
    /// </para>
    /// </remarks>
    private static void ConfigureSeedData(ModelBuilder modelBuilder)
    {
        // A fixed timestamp, not SYSUTCDATETIME(): seed data must be deterministic, or every
        // migration comparison would show a spurious difference.
        var seededAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<DataSource>().HasData(
            new DataSource
            {
                DataSourceId = DataSource.BlsCpiId,
                Code = DataSource.BlsCpiCode,
                Name = "US Consumer Price Index (CUUR0000SA0)",
                Publisher = "U.S. Bureau of Labor Statistics",
                LandingPageUrl = "https://www.bls.gov/data/home.htm",
                ApiEndpoint = "https://api.bls.gov/publicAPI/v2/timeseries/data/",
                AccessMethod = SourceAccessMethod.RestApi,
                HttpMethod = "POST",
                // Unregistered v2 calls work under a smaller quota, so a key raises the budget
                // rather than being a precondition.
                RequiresApiKey = false,
                PublicationCadence = "Monthly",
                CollectionIntervalMinutes = 60,
                TermsOfUseUrl = "https://www.bls.gov/developers/api_faqs.htm",
                IsEnabled = true,
                CreatedAtUtc = seededAt
            },
            new DataSource
            {
                DataSourceId = DataSource.NyFedSofrId,
                Code = DataSource.NyFedSofrCode,
                Name = "Secured Overnight Financing Rate",
                Publisher = "Federal Reserve Bank of New York",
                LandingPageUrl = "https://www.newyorkfed.org/markets/reference-rates/sofr",
                // The adapter appends the date range for the current calendar year; the stored
                // endpoint is the documentation of where the data comes from.
                ApiEndpoint = "https://markets.newyorkfed.org/api/rates/secured/sofr/search.json",
                AccessMethod = SourceAccessMethod.RestApi,
                HttpMethod = "GET",
                RequiresApiKey = false,
                PublicationCadence = "BusinessDaily",
                CollectionIntervalMinutes = 60,
                TermsOfUseUrl =
                    "https://www.newyorkfed.org/markets/reference-rates/terms-of-use-for-selected-rate-data",
                IsEnabled = true,
                CreatedAtUtc = seededAt
            });
    }

    // ---------------------------------------------------------------- collect

    private static void ConfigureDataSource(EntityTypeBuilder<DataSource> entity)
    {
        entity.ToTable("DataSource", "collect", t =>
        {
            t.HasCheckConstraint("CK_DataSource_Access",
                "[AccessMethod] IN ('RestApi','Html','Csv')");
            t.HasCheckConstraint("CK_DataSource_Method", "[HttpMethod] IN ('GET','POST')");
            t.HasCheckConstraint("CK_DataSource_Cadence",
                "[PublicationCadence] IN ('BusinessDaily','Daily','Weekly','Monthly','Quarterly','Annual','Irregular')");
            t.HasCheckConstraint("CK_DataSource_Timeout", "[RequestTimeoutSec] BETWEEN 1 AND 300");
            t.HasCheckConstraint("CK_DataSource_Interval",
                "[CollectionIntervalMinutes] BETWEEN 1 AND 1440");
        });

        entity.HasKey(e => e.DataSourceId);
        entity.Property(e => e.DataSourceId).ValueGeneratedNever();
        entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UQ_DataSource_Code");

        entity.Property(e => e.Code).HasMaxLength(20).IsUnicode(false).IsRequired();
        entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
        entity.Property(e => e.Publisher).HasMaxLength(100).IsRequired();
        entity.Property(e => e.LandingPageUrl).HasMaxLength(500).IsRequired();
        entity.Property(e => e.ApiEndpoint).HasMaxLength(1000).IsRequired();
        entity.Property(e => e.HttpMethod).HasMaxLength(6).IsUnicode(false).HasDefaultValue("GET");
        entity.Property(e => e.PublicationCadence).HasMaxLength(20).IsUnicode(false).IsRequired();
        entity.Property(e => e.UserAgent).HasMaxLength(250);
        entity.Property(e => e.TermsOfUseUrl).HasMaxLength(500);
        entity.Property(e => e.CollectionIntervalMinutes).HasDefaultValue((short)60);
        entity.Property(e => e.RequestTimeoutSec).HasDefaultValue((short)30);
        entity.Property(e => e.MaxRetries).HasDefaultValue((byte)3);
        entity.Property(e => e.IsEnabled).HasDefaultValue(true);
        entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");

        entity.Property(e => e.RequiresApiKey).HasDefaultValue(false);

        // Stored as strings so the schema's CHECK constraints stay readable and the AI
        // assistant's generated SQL can filter on 'RestApi' rather than an opaque integer.
        entity.Property(e => e.AccessMethod)
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false)
            .HasDefaultValue(SourceAccessMethod.RestApi);
    }

    private static void ConfigureCollectionRun(EntityTypeBuilder<CollectionRun> entity)
    {
        entity.ToTable("CollectionRun", "collect", t =>
        {
            // A finished run must say why it finished the way it did.
            t.HasCheckConstraint("CK_CollectionRun_FailureRequired",
                "[Status] <> 'Failed' OR [FailureCategory] IS NOT NULL");
            t.HasCheckConstraint("CK_CollectionRun_Completed",
                "[CompletedAtUtc] IS NULL OR [CompletedAtUtc] >= [StartedAtUtc]");
            t.HasCheckConstraint("CK_CollectionRun_Status",
                "[Status] IN ('Running','Succeeded','PartialSuccess','Failed','Skipped')");
            t.HasCheckConstraint("CK_CollectionRun_Trigger",
                "[TriggerType] IN ('Scheduled','Manual','Retry','Backfill')");
            t.HasCheckConstraint("CK_CollectionRun_Failure",
                "[FailureCategory] IS NULL OR [FailureCategory] IN "
                + "('Unreachable','Timeout','HttpError','RateLimited','ParseError','SchemaChanged',"
                + "'Validation','Persistence','Unknown')");
        });

        entity.HasKey(e => e.CollectionRunId);

        // Idempotency key, scoped per source so both publishers can share a cycle time.
        entity.HasIndex(e => new { e.DataSourceId, e.ScheduledForUtc, e.Attempt })
            .IsUnique()
            .HasDatabaseName("UQ_CollectionRun_Cycle");

        entity.Property(e => e.ScheduledForUtc).HasColumnType("datetime2(0)");
        entity.Property(e => e.StartedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        entity.Property(e => e.Attempt).HasDefaultValue((byte)1);
        entity.Property(e => e.RequestUrl).HasMaxLength(1000).IsRequired();
        entity.Property(e => e.ErrorMessage).HasMaxLength(1000);

        entity.Property(e => e.Status)
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false)
            .HasDefaultValue(CollectionRunStatus.Running);

        entity.Property(e => e.TriggerType)
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false)
            .HasDefaultValue(CollectionTriggerType.Scheduled);

        entity.Property(e => e.FailureCategory).HasConversion<string>().HasMaxLength(30).IsUnicode(false);

        // A run inserted directly in SQL — a backfill script, a repair — starts at zero rather
        // than failing on NOT NULL. Declared here as well as in the DDL so the migration and the
        // hand-written script stay byte-identical.
        entity.Property(e => e.ObservationsFetched).HasDefaultValue(0);
        entity.Property(e => e.ObservationsInserted).HasDefaultValue(0);
        entity.Property(e => e.ObservationsRevised).HasDefaultValue(0);
        entity.Property(e => e.ObservationsUnchanged).HasDefaultValue(0);
        entity.Property(e => e.ObservationsRejected).HasDefaultValue(0);

        entity.Property(e => e.DurationMs)
            .HasComputedColumnSql("DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc])");

        entity.HasOne(e => e.DataSource)
            .WithMany()
            .HasForeignKey(e => e.DataSourceId)
            .OnDelete(DeleteBehavior.NoAction);

        // Both indexes cover StartedAtUtc, so each needs an explicit model name — EF keys
        // indexes by property set, and the unnamed overload would let one replace the other.
        entity.HasIndex(e => e.StartedAtUtc, "IX_CollectionRun_StartedAtUtc")
            .IsDescending()
            .IncludeProperties(e => new { e.DataSourceId, e.Status, e.ObservationsInserted });

        entity.HasIndex(e => e.StartedAtUtc, "IX_CollectionRun_Failures")
            .IsDescending()
            .HasFilter("[Status] IN ('Failed','PartialSuccess')")
            .IncludeProperties(e => new
            {
                e.DataSourceId, e.FailureCategory, e.ErrorMessage, e.AlertSentAtUtc
            });
    }

    private static void ConfigureRawPayload(EntityTypeBuilder<RawPayload> entity)
    {
        entity.ToTable("RawPayload", "collect", t => t.HasCheckConstraint(
            "CK_RawPayload_Size", "[SizeBytes] >= 0"));

        entity.HasKey(e => e.RawPayloadId);

        entity.Property(e => e.FetchedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        entity.Property(e => e.ContentType).HasMaxLength(100);
        entity.Property(e => e.ContentHash).HasColumnType("binary(32)").IsRequired();
        entity.Property(e => e.CompressedContent).HasColumnType("varbinary(max)").IsRequired();

        entity.HasOne(e => e.Run)
            .WithMany(r => r.RawPayloads)
            .HasForeignKey(e => e.CollectionRunId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.CollectionRunId).HasDatabaseName("IX_RawPayload_Run");
        entity.HasIndex(e => new { e.ContentHash, e.FetchedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_RawPayload_Hash");
    }

    // ------------------------------------------------------------------- core

    /// <summary>
    /// BLS series CUUR0000SA0, one row per (year, period). See <see cref="CpiObservation"/> for
    /// why the annual and semiannual figures live here alongside the months.
    /// </summary>
    private static void ConfigureCpiObservation(EntityTypeBuilder<CpiObservation> entity)
    {
        entity.ToTable("CpiObservation", "core", t =>
        {
            t.HasCheckConstraint("CK_Cpi_SeriesCode",
                $"[SeriesCode] = '{CpiObservation.SeriesCodeValue}'");

            // Enumerated rather than a range: 'M1' sorts inside 'M01'..'M13' and would otherwise
            // be accepted as a second, silent spelling of January.
            t.HasCheckConstraint("CK_Cpi_PeriodCode",
                "[PeriodCode] IN ('M01','M02','M03','M04','M05','M06','M07','M08','M09','M10',"
                + "'M11','M12','M13','S01','S02')");

            // The token and its meaning must agree, or a filter on PeriodType silently lets an
            // annual average into a monthly trend.
            t.HasCheckConstraint("CK_Cpi_PeriodType",
                "([PeriodCode] BETWEEN 'M01' AND 'M12' AND [PeriodType] = 'Month') "
                + "OR ([PeriodCode] = 'M13' AND [PeriodType] = 'Annual') "
                + "OR ([PeriodCode] IN ('S01','S02') AND [PeriodType] = 'Semiannual')");

            // ReferenceDate is derived from (year, period) by the collector; this is the
            // assertion that the derivation was not skipped or mis-mapped.
            t.HasCheckConstraint("CK_Cpi_ReferenceDate",
                "DAY([ReferenceDate]) = 1 "
                + "AND YEAR([ReferenceDate]) = [ReferenceYear] "
                + "AND MONTH([ReferenceDate]) = "
                + "CASE WHEN [PeriodCode] BETWEEN 'M01' AND 'M12' "
                + "THEN CONVERT(INT, SUBSTRING([PeriodCode], 2, 2)) "
                + "WHEN [PeriodCode] = 'S02' THEN 7 ELSE 1 END");

            t.HasCheckConstraint("CK_Cpi_ReferenceYear", "[ReferenceYear] BETWEEN 1913 AND 2200");
            t.HasCheckConstraint("CK_Cpi_IndexValue", "[IndexValue] > 0");
            t.HasCheckConstraint("CK_Cpi_Revision", "[RevisionNumber] >= 0");

            // A superseded row is not current, and a current row is not superseded.
            t.HasCheckConstraint("CK_Cpi_Superseded",
                "([IsCurrent] = 1 AND [SupersededAtUtc] IS NULL) "
                + "OR ([IsCurrent] = 0 AND [SupersededAtUtc] IS NOT NULL)");
        });

        entity.HasKey(e => e.CpiObservationId).IsClustered(false).HasName("PK_CpiObservation");

        entity.Property(e => e.SeriesCode)
            .HasMaxLength(20).IsUnicode(false)
            .HasDefaultValue(CpiObservation.SeriesCodeValue);

        entity.Property(e => e.PeriodCode).HasMaxLength(3).IsUnicode(false).IsRequired();
        entity.Property(e => e.PeriodType).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        entity.Property(e => e.IndexValue).HasColumnType("decimal(12,3)");
        entity.Property(e => e.Footnotes).HasMaxLength(100).IsUnicode(false);
        entity.Property(e => e.RevisionNumber).HasDefaultValue((short)0);
        entity.Property(e => e.IsCurrent).HasDefaultValue(true);
        entity.Property(e => e.RowHash).HasColumnType("binary(32)").IsRequired();

        entity.HasOne(e => e.Run)
            .WithMany()
            .HasForeignKey(e => e.CollectionRunId)
            .OnDelete(DeleteBehavior.NoAction);

        // One row per period per vintage (FR-3), and clustered on the analytical axis, because
        // those want the same key: every chart and trend query is a date range over this one
        // series. PeriodCode is in the key because M01, M13 and S01 all start on 1 January, so
        // the date alone does not identify a period.
        entity.HasIndex(e => new { e.ReferenceDate, e.PeriodCode, e.RevisionNumber })
            .IsUnique()
            .IsClustered()
            .HasDatabaseName("UQ_Cpi_Vintage");

        // Exactly one current vintage per period. The integrity rule the dashboards depend on:
        // without it a botched revision could double-count a month unnoticed.
        entity.HasIndex(e => new { e.ReferenceYear, e.PeriodCode })
            .IsUnique()
            .HasFilter("[IsCurrent] = 1")
            .HasDatabaseName("UQ_CpiObservation_Current");

        entity.HasIndex(e => new { e.PeriodType, e.ReferenceDate }, "IX_CpiObservation_Monthly")
            .IsDescending(false, true)
            .IncludeProperties(e => new { e.IndexValue, e.RowHash, e.IsCurrent, e.RevisionNumber });

        entity.HasIndex(e => e.CollectionRunId).HasDatabaseName("IX_CpiObservation_Run");
    }

    /// <summary>
    /// SOFR, one row per business day. The six measures a day carries are columns, not rows —
    /// see <see cref="SofrDailyRate"/>.
    /// </summary>
    private static void ConfigureSofrDailyRate(EntityTypeBuilder<SofrDailyRate> entity)
    {
        entity.ToTable("SofrDailyRate", "core", t =>
        {
            t.HasCheckConstraint("CK_Sofr_RateType", $"[RateType] = '{SofrDailyRate.RateTypeValue}'");
            t.HasCheckConstraint("CK_Sofr_RevisionIndicator",
                "[RevisionIndicator] IS NULL OR [RevisionIndicator] IN ('Y','N')");

            // A decimal-shift parse bug produces 365 or 0.0365 where 3.65 was meant. The band is
            // deliberately far wider than any rate the Fed has ever set, so it catches the bug
            // without ever having an opinion on monetary policy.
            t.HasCheckConstraint("CK_Sofr_RateRange", "[RatePercent] BETWEEN -5 AND 25");
            t.HasCheckConstraint("CK_Sofr_Volume",
                "[VolumeUsdBillions] IS NULL OR [VolumeUsdBillions] >= 0");

            // Percentiles are ordered by definition. If they arrive out of order the columns have
            // been mapped to the wrong fields.
            t.HasCheckConstraint("CK_Sofr_PercentileOrder",
                "([Percentile1Percent] IS NULL OR [Percentile25Percent] IS NULL "
                + "OR [Percentile1Percent] <= [Percentile25Percent]) "
                + "AND ([Percentile25Percent] IS NULL OR [Percentile75Percent] IS NULL "
                + "OR [Percentile25Percent] <= [Percentile75Percent]) "
                + "AND ([Percentile75Percent] IS NULL OR [Percentile99Percent] IS NULL "
                + "OR [Percentile75Percent] <= [Percentile99Percent])");

            t.HasCheckConstraint("CK_Sofr_Revision", "[RevisionNumber] >= 0");
            t.HasCheckConstraint("CK_Sofr_Superseded",
                "([IsCurrent] = 1 AND [SupersededAtUtc] IS NULL) "
                + "OR ([IsCurrent] = 0 AND [SupersededAtUtc] IS NOT NULL)");
        });

        entity.HasKey(e => e.SofrDailyRateId).IsClustered(false).HasName("PK_SofrDailyRate");

        entity.Property(e => e.RateType)
            .HasMaxLength(5).IsUnicode(false)
            .HasDefaultValue(SofrDailyRate.RateTypeValue);

        entity.Property(e => e.RatePercent).HasColumnType("decimal(9,5)");
        entity.Property(e => e.Percentile1Percent).HasColumnType("decimal(9,5)");
        entity.Property(e => e.Percentile25Percent).HasColumnType("decimal(9,5)");
        entity.Property(e => e.Percentile75Percent).HasColumnType("decimal(9,5)");
        entity.Property(e => e.Percentile99Percent).HasColumnType("decimal(9,5)");
        entity.Property(e => e.VolumeUsdBillions).HasColumnType("decimal(12,3)");
        entity.Property(e => e.Average30DayPercent).HasColumnType("decimal(9,5)");
        entity.Property(e => e.Average90DayPercent).HasColumnType("decimal(9,5)");
        entity.Property(e => e.Average180DayPercent).HasColumnType("decimal(9,5)");
        entity.Property(e => e.SofrIndexValue).HasColumnType("decimal(20,8)");
        entity.Property(e => e.RevisionIndicator).HasColumnType("char(1)").IsUnicode(false);
        entity.Property(e => e.FootnoteId).HasMaxLength(20).IsUnicode(false);
        entity.Property(e => e.RevisionNumber).HasDefaultValue((short)0);
        entity.Property(e => e.IsCurrent).HasDefaultValue(true);
        entity.Property(e => e.RowHash).HasColumnType("binary(32)").IsRequired();

        entity.HasOne(e => e.Run)
            .WithMany()
            .HasForeignKey(e => e.CollectionRunId)
            .OnDelete(DeleteBehavior.NoAction);

        // As for CPI: the vintage key and the analytical ordering are the same key, so one
        // clustered unique index serves both.
        entity.HasIndex(e => new { e.EffectiveDate, e.RevisionNumber })
            .IsUnique()
            .IsClustered()
            .HasDatabaseName("UQ_Sofr_Vintage");

        // Exactly one current vintage per business day.
        entity.HasIndex(e => e.EffectiveDate)
            .IsUnique()
            .HasFilter("[IsCurrent] = 1")
            .HasDatabaseName("UQ_SofrDailyRate_Current");

        entity.HasIndex(e => e.CollectionRunId).HasDatabaseName("IX_SofrDailyRate_Run");
    }


    private static void ConfigureRejectedObservation(EntityTypeBuilder<RejectedObservation> entity)
    {
        entity.ToTable("RejectedObservation", "core", t => t.HasCheckConstraint(
            "CK_RejectedObservation_Reason",
            "[Reason] IN ('MissingField','TypeMismatch','OutOfRange','UnknownSeries',"
            + "'DuplicatePeriod','UnparseablePeriod','SchemaDrift','Unknown')"));

        entity.HasKey(e => e.RejectedObservationId);

        entity.Property(e => e.SeriesCode).HasMaxLength(100);
        entity.Property(e => e.ReferenceDateText).HasMaxLength(50);
        entity.Property(e => e.RejectedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        entity.Property(e => e.ReasonDetail).HasMaxLength(1000);
        entity.Property(e => e.Reason).HasConversion<string>().HasMaxLength(30).IsUnicode(false);

        entity.HasOne(e => e.Run)
            .WithMany(r => r.RejectedObservations)
            .HasForeignKey(e => e.CollectionRunId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.CollectionRunId, e.RejectedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_RejectedObservation_Run");
    }

    // --------------------------------------------------------------------- ai

    private static void ConfigureAssistantSession(EntityTypeBuilder<AssistantSession> entity)
    {
        entity.ToTable("AssistantSession", "ai");

        entity.HasKey(e => e.SessionId);
        entity.Property(e => e.SessionId).ValueGeneratedNever();
        entity.Property(e => e.StartedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        entity.Property(e => e.LastActivityAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");

        entity.HasIndex(e => new { e.UserId, e.StartedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_AssistantSession_User");
    }

    private static void ConfigureAssistantQuery(EntityTypeBuilder<AssistantQuery> entity)
    {
        entity.ToTable("AssistantQuery", "ai", t =>
        {
            t.HasCheckConstraint("CK_AssistantQuery_Validation",
                "[ValidationOutcome] IN ('Pending','Approved','RejectedNotSelect',"
                + "'RejectedForbiddenObject','RejectedSyntax','RejectedComplexity','RejectedNoSql',"
                + "'NotADataQuestion','RejectedUnreadableResponse')");
            t.HasCheckConstraint("CK_AssistantQuery_Execution",
                "[ExecutionStatus] IS NULL OR [ExecutionStatus] IN "
                + "('Succeeded','Failed','Timeout','Cancelled')");
            // The backstop behind ISqlSafetyValidator (SOW 9): even a bug in the validator cannot
            // record a statement as executed unless it was approved first.
            t.HasCheckConstraint("CK_AssistantQuery_NoUnvalidatedRun",
                "[WasExecuted] = 0 OR [ValidationOutcome] = 'Approved'");
        });

        entity.HasKey(e => e.AssistantQueryId);

        entity.Property(e => e.AskedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        entity.Property(e => e.QuestionText).HasMaxLength(2000).IsRequired();
        entity.Property(e => e.ValidationDetail).HasMaxLength(1000);
        entity.Property(e => e.Explanation).HasMaxLength(2000);
        entity.Property(e => e.ExecutionError).HasMaxLength(1000);
        entity.Property(e => e.ModelName).HasMaxLength(100);
        entity.Property(e => e.ClientIpHash).HasColumnType("binary(32)");
        entity.Property(e => e.WasExecuted).HasDefaultValue(false);

        // Stored as strings so the CHECK constraints above stay readable, matching how the
        // collection enums are persisted.
        // 30, not 20. 'RejectedForbiddenObject' is 23 characters, so at 20 the CHECK constraint
        // permitted a value the column could not physically hold: the first time the model wrote a
        // query against sec.* the audit insert would fail on truncation and take the request with
        // it — the one rejection the log most needs to record.
        entity.Property(e => e.ValidationOutcome)
            .HasConversion<string>().HasMaxLength(30).IsUnicode(false)
            .HasDefaultValue(AssistantValidationOutcome.Pending);
        entity.Property(e => e.ExecutionStatus)
            .HasConversion<string>().HasMaxLength(20).IsUnicode(false);

        entity.HasOne(e => e.Session)
            .WithMany(s => s.Queries)
            .HasForeignKey(e => e.SessionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Both of these cover AskedAtUtc. They are declared with the named HasIndex overload
        // because the expression-only overload identifies an index by its property list — calling
        // it twice for the same column reconfigures one index rather than declaring two, which
        // silently loses whichever was declared first.
        entity.HasIndex(e => e.AskedAtUtc, "IX_AssistantQuery_AskedAtUtc")
            .IsDescending(true);

        entity.HasIndex(e => new { e.UserId, e.AskedAtUtc }, "IX_AssistantQuery_User")
            .IsDescending(false, true);

        // The review queue (NFR Auditability): everything the validator turned away that is worth
        // a human's attention. NotADataQuestion is excluded deliberately — greetings would
        // otherwise dominate the queue by volume and bury the probes it exists to surface.
        //
        // Spelled as chained <> rather than NOT IN because a filtered index predicate does not
        // accept NOT IN — SQL Server rejects it at CREATE INDEX with a syntax error.
        entity.HasIndex(e => e.AskedAtUtc, "IX_AssistantQuery_Rejected")
            .IsDescending(true)
            .IncludeProperties(e => new { e.QuestionText, e.ValidationOutcome, e.ValidationDetail })
            .HasFilter(
                "[ValidationOutcome] <> 'Approved' AND [ValidationOutcome] <> 'Pending' "
                + "AND [ValidationOutcome] <> 'NotADataQuestion'");
    }

    private static void ConfigureAssistantFeedback(EntityTypeBuilder<AssistantFeedback> entity)
    {
        entity.ToTable("AssistantFeedback", "ai");

        entity.HasKey(e => e.AssistantQueryId);
        entity.Property(e => e.AssistantQueryId).ValueGeneratedNever();
        entity.Property(e => e.Comment).HasMaxLength(1000);
        entity.Property(e => e.SubmittedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");

        entity.HasOne(e => e.Query)
            .WithOne(q => q.Feedback)
            .HasForeignKey<AssistantFeedback>(e => e.AssistantQueryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
