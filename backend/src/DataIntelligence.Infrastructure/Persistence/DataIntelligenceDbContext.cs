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

    /// <summary>Read-only view of the turns inside every session's JSON transcript.</summary>
    public DbSet<AssistantTurn> AssistantTurns => Set<AssistantTurn>();

    /// <summary>Read-only list of conversations, for resuming one.</summary>
    public DbSet<AssistantSessionSummary> AssistantSessionSummaries => Set<AssistantSessionSummary>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Millisecond precision for every timestamp. EF's default is datetime2(7), which costs
        // two bytes a row for sub-millisecond resolution no publisher provides. Columns needing
        // something else (ScheduledForPkt) set it explicitly.
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
        ConfigureAssistantTurn(modelBuilder.Entity<AssistantTurn>());
        ConfigureAssistantSessionSummary(modelBuilder.Entity<AssistantSessionSummary>());

        // Turn ids. Declared on the model so migrations create it; allocated explicitly in
        // AssistantService rather than as a column default, since there is no column — the turn it
        // identifies is an object inside ai.AssistantSession.TranscriptJson.
        modelBuilder.HasSequence<long>("AssistantTurnId", "ai").StartsAt(1).IncrementsBy(1);

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
                CreatedAtPkt = seededAt
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
                CreatedAtPkt = seededAt
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
        entity.Property(e => e.CreatedAtPkt).HasDefaultValueSql("DATEADD(hour, 5, SYSUTCDATETIME())");

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
                "[CompletedAtPkt] IS NULL OR [CompletedAtPkt] >= [StartedAtPkt]");
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
        entity.HasIndex(e => new { e.DataSourceId, e.ScheduledForPkt, e.Attempt })
            .IsUnique()
            .HasDatabaseName("UQ_CollectionRun_Cycle");

        entity.Property(e => e.ScheduledForPkt).HasColumnType("datetime2(0)");
        entity.Property(e => e.StartedAtPkt).HasDefaultValueSql("DATEADD(hour, 5, SYSUTCDATETIME())");
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
            .HasComputedColumnSql("DATEDIFF_BIG(MILLISECOND, [StartedAtPkt], [CompletedAtPkt])");

        entity.HasOne(e => e.DataSource)
            .WithMany()
            .HasForeignKey(e => e.DataSourceId)
            .OnDelete(DeleteBehavior.NoAction);

        // Both indexes cover StartedAtPkt, so each needs an explicit model name — EF keys
        // indexes by property set, and the unnamed overload would let one replace the other.
        entity.HasIndex(e => e.StartedAtPkt, "IX_CollectionRun_StartedAtPkt")
            .IsDescending()
            .IncludeProperties(e => new { e.DataSourceId, e.Status, e.ObservationsInserted });

        entity.HasIndex(e => e.StartedAtPkt, "IX_CollectionRun_Failures")
            .IsDescending()
            .HasFilter("[Status] IN ('Failed','PartialSuccess')")
            .IncludeProperties(e => new
            {
                e.DataSourceId, e.FailureCategory, e.ErrorMessage, e.AlertSentAtPkt
            });
    }

    private static void ConfigureRawPayload(EntityTypeBuilder<RawPayload> entity)
    {
        entity.ToTable("RawPayload", "collect", t => t.HasCheckConstraint(
            "CK_RawPayload_Size", "[SizeBytes] >= 0"));

        entity.HasKey(e => e.RawPayloadId);

        entity.Property(e => e.FetchedAtPkt).HasDefaultValueSql("DATEADD(hour, 5, SYSUTCDATETIME())");
        entity.Property(e => e.ContentType).HasMaxLength(100);
        entity.Property(e => e.ContentHash).HasColumnType("binary(32)").IsRequired();
        entity.Property(e => e.CompressedContent).HasColumnType("varbinary(max)").IsRequired();

        entity.HasOne(e => e.Run)
            .WithMany(r => r.RawPayloads)
            .HasForeignKey(e => e.CollectionRunId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.CollectionRunId).HasDatabaseName("IX_RawPayload_Run");
        entity.HasIndex(e => new { e.ContentHash, e.FetchedAtPkt })
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
                "([IsCurrent] = 1 AND [SupersededAtPkt] IS NULL) "
                + "OR ([IsCurrent] = 0 AND [SupersededAtPkt] IS NOT NULL)");
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
                "([IsCurrent] = 1 AND [SupersededAtPkt] IS NULL) "
                + "OR ([IsCurrent] = 0 AND [SupersededAtPkt] IS NOT NULL)");
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
        entity.Property(e => e.RejectedAtPkt).HasDefaultValueSql("DATEADD(hour, 5, SYSUTCDATETIME())");
        entity.Property(e => e.ReasonDetail).HasMaxLength(1000);
        entity.Property(e => e.Reason).HasConversion<string>().HasMaxLength(30).IsUnicode(false);

        entity.HasOne(e => e.Run)
            .WithMany(r => r.RejectedObservations)
            .HasForeignKey(e => e.CollectionRunId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.CollectionRunId, e.RejectedAtPkt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_RejectedObservation_Run");
    }

    // --------------------------------------------------------------------- ai

    private static void ConfigureAssistantSession(EntityTypeBuilder<AssistantSession> entity)
    {
        // Enforced in the database rather than trusted from the application, because this column
        // is a document: anything that writes a malformed string here is discovered by whoever
        // tries to read the transcript back, which may be long after the write that broke it.
        // ISJSON is the cheapest possible check and rejects it at the point of damage instead.
        entity.ToTable("AssistantSession", "ai", t =>
            t.HasCheckConstraint("CK_AssistantSession_TranscriptJson",
                "[TranscriptJson] IS NULL OR ISJSON([TranscriptJson]) = 1"));

        entity.HasKey(e => e.SessionId);
        entity.Property(e => e.SessionId).ValueGeneratedNever();
        entity.Property(e => e.StartedAtPkt).HasDefaultValueSql("DATEADD(hour, 5, SYSUTCDATETIME())");
        entity.Property(e => e.LastActivityAtPkt).HasDefaultValueSql("DATEADD(hour, 5, SYSUTCDATETIME())");

        entity.HasIndex(e => new { e.UserId, e.StartedAtPkt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_AssistantSession_User");
    }

    /// <summary>
    /// The read model over <c>ai.AssistantSession.TranscriptJson</c>: one row per turn, shredded
    /// out of the stored document so the audit log can be filtered and paged in SQL.
    /// </summary>
    /// <remarks>
    /// <c>OPENJSON ... WITH</c> rather than <c>JSON_VALUE</c> per column, because the WITH form
    /// parses each document once and projects every column from that single parse; a column list of
    /// JSON_VALUE calls re-parses the whole document once per column per row.
    /// <para>
    /// The path strings are the contract with <c>ChatTranscriptWriter</c>'s camelCase output, and
    /// the two have to be changed together. A renamed property here silently yields NULL rather
    /// than an error — OPENJSON in non-strict mode treats a missing path as absent, not as a fault.
    /// </para>
    /// </remarks>
    private static void ConfigureAssistantTurn(EntityTypeBuilder<AssistantTurn> entity)
    {
        // ExcludeFromMigrations as well as ToSqlQuery. The DbSet gives the type a table mapping by
        // convention, and ToSqlQuery adds the query on top rather than replacing it — so without
        // this, migrations helpfully create a real ai.AssistantTurns table that nothing would ever
        // write to and the query would ignore.
        entity.ToTable("AssistantTurns", "ai", t => t.ExcludeFromMigrations());

        entity.HasNoKey().ToSqlQuery("""
            SELECT  t.AssistantQueryId,
                    s.SessionId,
                    s.UserId,
                    -- Documents written before the clock moved to PKT carry askedAtUtc and a UTC
                    -- reading. Shifted rather than merely coalesced: taking the old value as-is
                    -- would file those turns five hours early and sort them among the wrong ones,
                    -- which is a subtler failure than the NULL it would otherwise be.
                    ISNULL(t.AskedAtPkt, DATEADD(hour, 5, t.AskedAtLegacyUtc)) AS AskedAtPkt,
                    t.QuestionText,
                    t.AnswerText,
                    t.ValidationOutcome,
                    t.ValidationDetail,
                    t.GeneratedSql,
                    t.SqlParametersJson,
                    t.Explanation,

                    -- Backfilled, not read straight through. A JSON store has no migration step:
                    -- documents written before a field existed simply lack it, OPENJSON returns
                    -- NULL, and a NULL into a non-nullable bool throws rather than degrading. Turns
                    -- written before wasExecuted was recorded still carry an execution status, and
                    -- having one is exactly what being executed means — so the older shape can be
                    -- answered accurately instead of merely defaulted.
                    --
                    -- Every reader of this column has to keep tolerating shapes it did not write.
                    -- That is the standing cost of the store being a document.
                    ISNULL(t.WasExecuted,
                           CASE WHEN t.ExecutionStatus IS NOT NULL THEN 1 ELSE 0 END) AS WasExecuted,
                    t.ExecutionStatus,
                    t.ExecutionError,
                    t.ExecutionMs,
                    t.ResultRowCount,
                    t.ModelChoice,
                    t.ModelName,
                    t.PromptTokens,
                    t.CompletionTokens,
                    t.TotalTokens,
                    t.TotalLatencyMs
            FROM    ai.AssistantSession AS s
            CROSS APPLY OPENJSON(s.TranscriptJson, '$.turns')
            WITH (
                    AssistantQueryId    BIGINT          '$.assistantQueryId',
                    AskedAtPkt          DATETIME2(3)    '$.askedAtPkt',
                    AskedAtLegacyUtc    DATETIME2(3)    '$.askedAtUtc',
                    QuestionText        NVARCHAR(2000)  '$.question',
                    AnswerText          NVARCHAR(MAX)   '$.answer',
                    ValidationOutcome   VARCHAR(30)     '$.outcome',
                    ValidationDetail    NVARCHAR(1000)  '$.validationDetail',
                    GeneratedSql        NVARCHAR(MAX)   '$.sql',
                    SqlParametersJson   NVARCHAR(MAX)   '$.parameters' AS JSON,
                    Explanation         NVARCHAR(2000)  '$.explanation',
                    WasExecuted         BIT             '$.wasExecuted',
                    ExecutionStatus     VARCHAR(20)     '$.executionStatus',
                    ExecutionError      NVARCHAR(1000)  '$.executionError',
                    ExecutionMs         INT             '$.executionMs',
                    ResultRowCount      INT             '$.resultRowCount',

                    -- NULL on every turn written before the model became a choice, which is the
                    -- honest reading: those turns reached the only gateway there was, but the
                    -- document does not say so and backfilling a value here would be asserting it.
                    ModelChoice         VARCHAR(20)     '$.modelChoice',
                    ModelName           NVARCHAR(100)   '$.modelName',
                    PromptTokens        INT             '$.promptTokens',
                    CompletionTokens    INT             '$.completionTokens',
                    TotalTokens         INT             '$.totalTokens',
                    TotalLatencyMs      INT             '$.totalLatencyMs'
            ) AS t
            """);

        entity.Property(e => e.ValidationOutcome).HasConversion<string>();
        entity.Property(e => e.ExecutionStatus).HasConversion<string>();
        entity.Property(e => e.ModelChoice).HasConversion<string>();
    }

    /// <summary>
    /// The conversation list: one row per session, read out of the transcript without shredding it.
    /// </summary>
    /// <remarks>
    /// Sessions with no transcript are excluded rather than shown as empty. One is created the
    /// moment a question arrives and before the answer is written, so a crashed or abandoned first
    /// question leaves a session behind with nothing in it — offering that back as a conversation
    /// to resume would be offering a blank page.
    /// <para>
    /// TRY_CAST on the count, not CAST: turnCount comes out of a document the database does not
    /// validate the shape of, and one malformed transcript should cost its own row rather than
    /// failing the whole list for that user.
    /// </para>
    /// </remarks>
    private static void ConfigureAssistantSessionSummary(
        EntityTypeBuilder<AssistantSessionSummary> entity)
    {
        entity.ToTable("AssistantSessionSummaries", "ai", t => t.ExcludeFromMigrations());

        entity.HasNoKey().ToSqlQuery("""
            SELECT  s.SessionId,
                    s.UserId,
                    s.StartedAtPkt,
                    s.LastActivityAtPkt,
                    ISNULL(TRY_CAST(JSON_VALUE(s.TranscriptJson, '$.turnCount') AS INT), 0) AS TurnCount,
                    JSON_VALUE(s.TranscriptJson, '$.turns[0].question') AS Title,

                    -- The column, not a SUM over the turns. Deriving it here would shred every one
                    -- of a user's transcripts to add up one integer per conversation, which is the
                    -- cost this list exists to avoid. AssistantService keeps the column current.
                    s.TotalTokens
            FROM    ai.AssistantSession AS s
            WHERE   s.TranscriptJson IS NOT NULL
            """);
    }
}
