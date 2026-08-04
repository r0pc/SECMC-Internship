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
/// The <c>sec</c> and <c>ai</c> schemas are not mapped here — they belong to the
/// authentication and AI-assistant work and are untouched by collection.
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
    public DbSet<SeriesCategory> SeriesCategories => Set<SeriesCategory>();
    public DbSet<Series> Series => Set<Series>();
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<RejectedObservation> RejectedObservations => Set<RejectedObservation>();

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
        ConfigureSeriesCategory(modelBuilder.Entity<SeriesCategory>());
        ConfigureSeries(modelBuilder.Entity<Series>());
        ConfigureObservation(modelBuilder.Entity<Observation>());
        ConfigureRejectedObservation(modelBuilder.Entity<RejectedObservation>());

        ConfigureSeedData(modelBuilder);
    }

    /// <summary>
    /// The designated sources and their series (SOW 0.1), seeded through the migration so a
    /// fresh deployment has something to collect from.
    /// </summary>
    /// <remarks>
    /// Reference data rather than user configuration: the platform is commissioned against these
    /// two publishers, and seeding here keeps identifiers stable across environments so config
    /// and logs can refer to a source by code. Mirrors section 7 of the hand-written schema.
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
                Name = "US Consumer Price Index",
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
                ApiEndpoint = "https://markets.newyorkfed.org/api/rates/secured/sofr/last/10.json",
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

        modelBuilder.Entity<SeriesCategory>().HasData(
            new SeriesCategory { CategoryId = 1, Code = "cpi-headline", DisplayName = "CPI — All items", SortOrder = 10, CreatedAtUtc = seededAt },
            new SeriesCategory { CategoryId = 2, Code = "cpi-core", DisplayName = "CPI — All items less food and energy", SortOrder = 20, CreatedAtUtc = seededAt },
            new SeriesCategory { CategoryId = 3, Code = "sofr-rate", DisplayName = "SOFR — Rate", SortOrder = 30, CreatedAtUtc = seededAt },
            new SeriesCategory { CategoryId = 4, Code = "sofr-liquidity", DisplayName = "SOFR — Volume and distribution", SortOrder = 40, CreatedAtUtc = seededAt });

        const string cpiUnit = "Index 1982-84=100";
        const string cpiUrl = "https://www.bls.gov/cpi/";
        const string sofrUrl = "https://www.newyorkfed.org/markets/reference-rates/sofr";
        const string rateUnit = "Percent per annum";

        modelBuilder.Entity<Series>().HasData(
            // CPI: SeriesCode is the BLS series ID. Both adjusted and unadjusted variants are
            // tracked — SA is the right basis for month-over-month, NSA for year-over-year.
            NewSeries(1, DataSource.BlsCpiId, "CUUR0000SA0", true, null,
                "CPI-U, All items, US city average, not seasonally adjusted", 1, cpiUnit, 3,
                SeriesFrequency.Monthly, SeasonalAdjustment.NotSeasonallyAdjusted, cpiUrl),
            NewSeries(2, DataSource.BlsCpiId, "CUSR0000SA0", true, null,
                "CPI-U, All items, US city average, seasonally adjusted", 1, cpiUnit, 3,
                SeriesFrequency.Monthly, SeasonalAdjustment.SeasonallyAdjusted, cpiUrl),
            NewSeries(3, DataSource.BlsCpiId, "CUUR0000SA0L1E", true, null,
                "CPI-U, All items less food and energy, not seasonally adjusted", 2, cpiUnit, 3,
                SeriesFrequency.Monthly, SeasonalAdjustment.NotSeasonallyAdjusted, cpiUrl),
            NewSeries(4, DataSource.BlsCpiId, "CUSR0000SA0L1E", true, null,
                "CPI-U, All items less food and energy, seasonally adjusted", 2, cpiUnit, 3,
                SeriesFrequency.Monthly, SeasonalAdjustment.SeasonallyAdjusted, cpiUrl),

            // SOFR: one API record carries six measures, so each gets a platform-assigned code
            // and records the field it is read from.
            NewSeries(5, DataSource.NyFedSofrId, "SOFR", false, "percentRate",
                "SOFR, overnight rate", 3, rateUnit, 2,
                SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, sofrUrl),
            NewSeries(6, DataSource.NyFedSofrId, "SOFR_VOL", false, "volumeInBillions",
                "SOFR, transaction volume", 4, "USD billions", 0,
                SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, sofrUrl),
            NewSeries(7, DataSource.NyFedSofrId, "SOFR_P1", false, "percentPercentile1",
                "SOFR, 1st percentile", 4, rateUnit, 2,
                SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, sofrUrl),
            NewSeries(8, DataSource.NyFedSofrId, "SOFR_P25", false, "percentPercentile25",
                "SOFR, 25th percentile", 4, rateUnit, 2,
                SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, sofrUrl),
            NewSeries(9, DataSource.NyFedSofrId, "SOFR_P75", false, "percentPercentile75",
                "SOFR, 75th percentile", 4, rateUnit, 2,
                SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, sofrUrl),
            NewSeries(10, DataSource.NyFedSofrId, "SOFR_P99", false, "percentPercentile99",
                "SOFR, 99th percentile", 4, rateUnit, 2,
                SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, sofrUrl));

        static Series NewSeries(
            int id, byte sourceId, string code, bool sourceAssigned, string? fieldPath,
            string title, int categoryId, string unit, byte decimals,
            SeriesFrequency frequency, SeasonalAdjustment seasonal, string url) =>
            new()
            {
                SeriesId = id,
                DataSourceId = sourceId,
                SeriesCode = code,
                IsSourceAssignedCode = sourceAssigned,
                SourceFieldPath = fieldPath,
                Title = title,
                CategoryId = categoryId,
                Unit = unit,
                DecimalPlaces = decimals,
                Frequency = frequency,
                SeasonalAdjustment = seasonal,
                SourceUrl = url,
                IsActive = true
            };
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

        // Stored as strings so the schema's CHECK constraints stay readable and the AI
        // assistant's generated SQL can filter on 'RestApi' rather than an opaque integer.
        entity.Property(e => e.AccessMethod).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
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

        entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        entity.Property(e => e.TriggerType).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        entity.Property(e => e.FailureCategory).HasConversion<string>().HasMaxLength(30).IsUnicode(false);

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

    private static void ConfigureSeriesCategory(EntityTypeBuilder<SeriesCategory> entity)
    {
        entity.ToTable("SeriesCategory", "core", t => t.HasCheckConstraint(
            "CK_SeriesCategory_NotSelfParent", "[ParentCategoryId] <> [CategoryId]"));

        entity.HasKey(e => e.CategoryId);
        entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UQ_SeriesCategory_Code");

        entity.Property(e => e.Code).HasMaxLength(100).IsRequired();
        entity.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
        entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");

        entity.HasOne(e => e.Parent)
            .WithMany(e => e.Children)
            .HasForeignKey(e => e.ParentCategoryId)
            .OnDelete(DeleteBehavior.NoAction);

        // Filtered: most series sit at the top level, so an unfiltered index would be dead weight.
        entity.HasIndex(e => e.ParentCategoryId, "IX_SeriesCategory_Parent")
            .HasFilter("[ParentCategoryId] IS NOT NULL");
    }

    private static void ConfigureSeries(EntityTypeBuilder<Series> entity)
    {
        entity.ToTable("Series", "core", t =>
        {
            t.HasCheckConstraint("CK_Series_Frequency",
                "[Frequency] IN ('BusinessDaily','Daily','Weekly','Monthly','Quarterly','Semiannual','Annual')");
            t.HasCheckConstraint("CK_Series_Seasonal",
                "[SeasonalAdjustment] IN ('SeasonallyAdjusted','NotSeasonallyAdjusted','NotApplicable')");
            t.HasCheckConstraint("CK_Series_SeenOrder",
                "[LastSeenAtUtc] IS NULL OR [FirstSeenAtUtc] IS NULL OR [LastSeenAtUtc] >= [FirstSeenAtUtc]");
            // A platform-assigned code must say where it came from, or the mapping is lost.
            t.HasCheckConstraint("CK_Series_FieldPath",
                "[IsSourceAssignedCode] = 1 OR [SourceFieldPath] IS NOT NULL");
        });

        entity.HasKey(e => e.SeriesId);

        // The dedup anchor (FR-3), scoped per source so two publishers may reuse a code.
        entity.HasIndex(e => new { e.DataSourceId, e.SeriesCode })
            .IsUnique()
            .HasDatabaseName("UQ_Series_Code");

        entity.Property(e => e.SeriesCode).HasMaxLength(100).IsRequired();
        entity.Property(e => e.SourceFieldPath).HasMaxLength(200);
        entity.Property(e => e.Title).HasMaxLength(400).IsRequired();
        entity.Property(e => e.Unit).HasMaxLength(60).IsRequired();
        entity.Property(e => e.SourceUrl).HasMaxLength(1000);
        entity.Property(e => e.IsActive).HasDefaultValue(true);
        entity.Property(e => e.IsSourceAssignedCode).HasDefaultValue(true);

        entity.Property(e => e.Frequency).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        entity.Property(e => e.SeasonalAdjustment).HasConversion<string>().HasMaxLength(24).IsUnicode(false);

        // SQL Server always populates a rowversion column, so it is NOT NULL in the database
        // even though the CLR property is nullable until the entity is first saved.
        entity.Property(e => e.RowVersion).IsRowVersion().IsRequired();

        entity.HasOne(e => e.DataSource)
            .WithMany(d => d.Series)
            .HasForeignKey(e => e.DataSourceId)
            .OnDelete(DeleteBehavior.NoAction);

        entity.HasOne(e => e.Category)
            .WithMany(c => c.Series)
            .HasForeignKey(e => e.CategoryId)
            .OnDelete(DeleteBehavior.NoAction);

        entity.HasOne<CollectionRun>()
            .WithMany()
            .HasForeignKey(e => e.FirstSeenRunId)
            .OnDelete(DeleteBehavior.NoAction);

        entity.HasIndex(e => new { e.CategoryId, e.IsActive })
            .IncludeProperties(e => e.Title)
            .HasDatabaseName("IX_Series_Category");

        entity.HasIndex(e => new { e.DataSourceId, e.IsActive })
            .IncludeProperties(e => new { e.SeriesCode, e.Title })
            .HasDatabaseName("IX_Series_Source");
    }

    private static void ConfigureObservation(EntityTypeBuilder<Observation> entity)
    {
        entity.ToTable("Observation", "core", t =>
        {
            t.HasCheckConstraint("CK_Observation_PeriodType",
                "[PeriodType] IN ('Day','Week','Month','Quarter','Semiannual','Annual')");
            t.HasCheckConstraint("CK_Observation_Revision", "[RevisionNumber] >= 0");
            // A superseded row is not current, and a current row is not superseded.
            t.HasCheckConstraint("CK_Observation_Superseded",
                "([IsCurrent] = 1 AND [SupersededAtUtc] IS NULL) "
                + "OR ([IsCurrent] = 0 AND [SupersededAtUtc] IS NOT NULL)");
        });

        // ReferenceDate is carried in every unique key so the table can be partitioned on it
        // later without redesign (NFR Scalability).
        entity.HasKey(e => new { e.ObservationId, e.ReferenceDate })
            .IsClustered(false)
            .HasName("PK_Observation");

        entity.Property(e => e.ObservationId).UseIdentityColumn();

        entity.HasIndex(e => new { e.SeriesId, e.ReferenceDate, e.RevisionNumber })
            .IsUnique()
            .IsClustered(false)
            .HasDatabaseName("UQ_Observation_Vintage");

        // Exactly one current vintage per (series, period). The integrity rule the dashboards
        // depend on: without it a botched revision could double-count a period unnoticed.
        entity.HasIndex(e => new { e.SeriesId, e.ReferenceDate })
            .IsUnique()
            .HasFilter("[IsCurrent] = 1")
            .HasDatabaseName("UQ_Observation_Current");

        entity.Property(e => e.Value).HasColumnType("decimal(28,8)");
        entity.Property(e => e.SourcePeriodCode).HasMaxLength(6).IsUnicode(false);
        entity.Property(e => e.SourceAnnotation).HasMaxLength(100).IsUnicode(false);
        entity.Property(e => e.RowHash).HasColumnType("binary(32)").IsRequired();
        entity.Property(e => e.IsCurrent).HasDefaultValue(true);
        entity.Property(e => e.RevisionNumber).HasDefaultValue((short)0);
        entity.Property(e => e.PeriodType).HasConversion<string>().HasMaxLength(12).IsUnicode(false);

        // Derived from a NOT NULL column so it can never be null; EF omits NOT NULL on computed
        // columns, which is why the hand-written DDL omits it too — the two must stay identical.
        entity.Property(e => e.ReferenceDateKey)
            .HasComputedColumnSql("CONVERT(INT, CONVERT(CHAR(8), [ReferenceDate], 112))", stored: true);

        entity.HasOne(e => e.Series)
            .WithMany(s => s.Observations)
            .HasForeignKey(e => e.SeriesId)
            .OnDelete(DeleteBehavior.NoAction);

        entity.HasOne(e => e.Run)
            .WithMany()
            .HasForeignKey(e => e.CollectionRunId)
            .OnDelete(DeleteBehavior.NoAction);

        // Clustered on the analytical axis: every dashboard query is a series over a date range,
        // and this is the partition-aligned ordering.
        entity.HasIndex(e => new { e.ReferenceDate, e.SeriesId })
            .IsClustered()
            .HasDatabaseName("CIX_Observation_Reference");

        entity.HasIndex(e => new { e.SeriesId, e.ReferenceDate }, "IX_Observation_Series_Reference")
            .IsDescending(false, true)
            .IncludeProperties(e => new { e.Value, e.RowHash, e.IsCurrent, e.PeriodType });

        entity.HasIndex(e => e.CollectionRunId).HasDatabaseName("IX_Observation_Run");
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
}
