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
/// authentication and AI-assistant work and are not touched by collection.
/// </remarks>
public class DataIntelligenceDbContext : DbContext
{
    public DataIntelligenceDbContext(DbContextOptions<DataIntelligenceDbContext> options)
        : base(options)
    {
    }

    public DbSet<SourceConfig> SourceConfigs => Set<SourceConfig>();
    public DbSet<CollectionRun> CollectionRuns => Set<CollectionRun>();
    public DbSet<RawPayload> RawPayloads => Set<RawPayload>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ItemSnapshot> ItemSnapshots => Set<ItemSnapshot>();
    public DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();
    public DbSet<SnapshotAttribute> SnapshotAttributes => Set<SnapshotAttribute>();
    public DbSet<RejectedRecord> RejectedRecords => Set<RejectedRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Millisecond precision for every timestamp. EF's default is datetime2(7), which costs
        // two bytes a row for sub-millisecond resolution no web source can meaningfully provide.
        // Columns needing something else (ScheduledForUtc) set it explicitly.
        configurationBuilder.Properties<DateTime>().HavePrecision(3);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureSourceConfig(modelBuilder.Entity<SourceConfig>());
        ConfigureCollectionRun(modelBuilder.Entity<CollectionRun>());
        ConfigureRawPayload(modelBuilder.Entity<RawPayload>());
        ConfigureCategory(modelBuilder.Entity<Category>());
        ConfigureItem(modelBuilder.Entity<Item>());
        ConfigureItemSnapshot(modelBuilder.Entity<ItemSnapshot>());
        ConfigureAttributeDefinition(modelBuilder.Entity<AttributeDefinition>());
        ConfigureSnapshotAttribute(modelBuilder.Entity<SnapshotAttribute>());
        ConfigureRejectedRecord(modelBuilder.Entity<RejectedRecord>());
    }

    // ---------------------------------------------------------------- collect

    private static void ConfigureSourceConfig(EntityTypeBuilder<SourceConfig> entity)
    {
        entity.ToTable("SourceConfig", "collect", t =>
        {
            t.HasCheckConstraint("CK_SourceConfig_Single", $"[SourceConfigId] = {SourceConfig.SingletonId}");
            t.HasCheckConstraint("CK_SourceConfig_Timeout", "[RequestTimeoutSec] BETWEEN 1 AND 300");
        });

        entity.HasKey(e => e.SourceConfigId);

        // Never generated: the row's identity is the constant 1.
        entity.Property(e => e.SourceConfigId).ValueGeneratedNever();
        entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
        entity.Property(e => e.BaseUrl).HasMaxLength(500).IsRequired();
        entity.Property(e => e.CollectionUrl).HasMaxLength(1000).IsRequired();
        entity.Property(e => e.UserAgent).HasMaxLength(250);
        entity.Property(e => e.CollectionIntervalMinutes).HasDefaultValue((short)60);
        entity.Property(e => e.RequestTimeoutSec).HasDefaultValue((short)30);
        entity.Property(e => e.MaxRetries).HasDefaultValue((byte)3);
        entity.Property(e => e.IsEnabled).HasDefaultValue(true);
        entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
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

            // The enums are stored as strings, so the database enforces the same value sets the
            // code does. Without these a bad deployment could write a status nothing can read.
            t.HasCheckConstraint("CK_CollectionRun_Status",
                "[Status] IN ('Running','Succeeded','PartialSuccess','Failed','Skipped')");
            t.HasCheckConstraint("CK_CollectionRun_Trigger",
                "[TriggerType] IN ('Scheduled','Manual','Retry','Backfill')");
            t.HasCheckConstraint("CK_CollectionRun_Failure",
                "[FailureCategory] IS NULL OR [FailureCategory] IN "
                + "('Unreachable','Timeout','HttpError','ParseError','LayoutChanged','Validation',"
                + "'Persistence','Unknown')");
        });

        entity.HasKey(e => e.CollectionRunId);

        // Idempotency key: identifies the logical cycle a run belongs to (FR-3).
        entity.HasIndex(e => new { e.ScheduledForUtc, e.Attempt })
            .IsUnique()
            .HasDatabaseName("UQ_CollectionRun_Cycle");

        entity.Property(e => e.ScheduledForUtc).HasColumnType("datetime2(0)");
        entity.Property(e => e.StartedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        entity.Property(e => e.Attempt).HasDefaultValue((byte)1);
        entity.Property(e => e.RequestUrl).HasMaxLength(1000).IsRequired();
        entity.Property(e => e.ErrorMessage).HasMaxLength(1000);

        // Enums are stored as strings so the schema's CHECK constraints stay readable and the
        // AI assistant's generated SQL can filter on 'Failed' rather than an opaque integer.
        entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        entity.Property(e => e.TriggerType).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        entity.Property(e => e.FailureCategory).HasConversion<string>().HasMaxLength(30).IsUnicode(false);

        // Computed by SQL Server from the two timestamps.
        entity.Property(e => e.DurationMs)
            .HasComputedColumnSql("DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc])");

        // Both indexes cover StartedAtUtc, so each needs an explicit model name — EF keys indexes
        // by property set, and the unnamed overload would let the second silently replace the first.
        entity.HasIndex(e => e.StartedAtUtc, "IX_CollectionRun_StartedAtUtc")
            .IsDescending()
            .IncludeProperties(e => new { e.Status, e.RecordsInserted });

        // Failures are rare; a filtered index keeps the health and alerting queries cheap.
        entity.HasIndex(e => e.StartedAtUtc, "IX_CollectionRun_Failures")
            .IsDescending()
            .HasFilter("[Status] IN ('Failed','PartialSuccess')")
            .IncludeProperties(e => new { e.FailureCategory, e.ErrorMessage, e.AlertSentAtUtc });
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
        entity.HasIndex(e => new { e.ContentHash, e.FetchedAtUtc }).HasDatabaseName("IX_RawPayload_Hash");
    }

    // ------------------------------------------------------------------- core

    private static void ConfigureCategory(EntityTypeBuilder<Category> entity)
    {
        entity.ToTable("Category", "core", t => t.HasCheckConstraint(
            "CK_Category_NotSelfParent", "[ParentCategoryId] <> [CategoryId]"));

        entity.HasKey(e => e.CategoryId);
        entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UQ_Category_Code");

        entity.Property(e => e.Code).HasMaxLength(100).IsRequired();
        entity.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
        entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");

        // NoAction: a cycle in the hierarchy should be rejected, not cascade-deleted.
        entity.HasOne(e => e.Parent)
            .WithMany(e => e.Children)
            .HasForeignKey(e => e.ParentCategoryId)
            .OnDelete(DeleteBehavior.NoAction);

        // Filtered: most sources have a flat taxonomy, so almost every row is null here and an
        // unfiltered index would be mostly dead weight.
        entity.HasIndex(e => e.ParentCategoryId, "IX_Category_Parent")
            .HasFilter("[ParentCategoryId] IS NOT NULL");
    }

    private static void ConfigureItem(EntityTypeBuilder<Item> entity)
    {
        entity.ToTable("Item", "core", t => t.HasCheckConstraint(
            "CK_Item_SeenOrder", "[LastSeenAtUtc] >= [FirstSeenAtUtc]"));

        entity.HasKey(e => e.ItemId);

        // The dedup anchor (FR-3).
        entity.HasIndex(e => e.SourceKey).IsUnique().HasDatabaseName("UQ_Item_SourceKey");

        entity.Property(e => e.SourceKey).HasMaxLength(200).IsRequired();
        entity.Property(e => e.Title).HasMaxLength(400).IsRequired();
        entity.Property(e => e.SourceUrl).HasMaxLength(1000);
        entity.Property(e => e.IsActive).HasDefaultValue(true);

        // SQL Server always populates a rowversion column, so it is NOT NULL in the database
        // even though the CLR property is nullable until the entity is first saved.
        entity.Property(e => e.RowVersion).IsRowVersion().IsRequired();

        entity.HasOne(e => e.Category)
            .WithMany(c => c.Items)
            .HasForeignKey(e => e.CategoryId)
            .OnDelete(DeleteBehavior.NoAction);

        entity.HasIndex(e => new { e.CategoryId, e.IsActive })
            .IncludeProperties(e => e.Title)
            .HasDatabaseName("IX_Item_Category");

        entity.HasIndex(e => e.LastSeenAtUtc).IsDescending().HasDatabaseName("IX_Item_LastSeen");
    }

    private static void ConfigureItemSnapshot(EntityTypeBuilder<ItemSnapshot> entity)
    {
        entity.ToTable("ItemSnapshot", "core", t => t.HasCheckConstraint(
            "CK_ItemSnapshot_Quantity", "[Quantity] IS NULL OR [Quantity] >= 0"));

        // CollectedAtUtc is carried in the key so the table can be partitioned on it later
        // without a redesign (NFR Scalability) — a partitioned table requires the partition
        // column in every unique index.
        entity.HasKey(e => new { e.ItemSnapshotId, e.CollectedAtUtc })
            .IsClustered(false)
            .HasName("PK_ItemSnapshot");

        entity.Property(e => e.ItemSnapshotId).UseIdentityColumn();
        entity.Property(e => e.CollectedAtUtc).HasColumnType("datetime2(3)");

        // FR-3: re-running a cycle cannot create a second row for the same item.
        entity.HasIndex(e => new { e.ItemId, e.CollectionRunId, e.CollectedAtUtc })
            .IsUnique()
            .IsClustered(false)
            .HasDatabaseName("UQ_ItemSnapshot_ItemRun");

        // Derived from a NOT NULL column, so it is never null; EF treats computed columns as
        // nullable unless told otherwise, which would let a nullable int leak into read models.
        entity.Property(e => e.CollectedDateKey)
            .HasComputedColumnSql("CONVERT(INT, CONVERT(CHAR(8), [CollectedAtUtc], 112))", stored: true)
            .IsRequired();

        entity.Property(e => e.PrimaryValue).HasColumnType("decimal(18,4)");
        entity.Property(e => e.SecondaryValue).HasColumnType("decimal(18,4)");
        entity.Property(e => e.StatusText).HasMaxLength(100);
        entity.Property(e => e.CurrencyCode).HasColumnType("char(3)");
        entity.Property(e => e.RowHash).HasColumnType("binary(32)").IsRequired();
        entity.Property(e => e.HasChanged).HasDefaultValue(true);

        entity.HasOne(e => e.Item)
            .WithMany(i => i.Snapshots)
            .HasForeignKey(e => e.ItemId)
            .OnDelete(DeleteBehavior.NoAction);

        entity.HasOne(e => e.Run)
            .WithMany()
            .HasForeignKey(e => e.CollectionRunId)
            .OnDelete(DeleteBehavior.NoAction);

        // Clustered on time first: every dashboard query is date-ranged, and this is the
        // partition-aligned ordering. Page compression because snapshots repeat run over run.
        entity.HasIndex(e => new { e.CollectedAtUtc, e.ItemId })
            .IsClustered()
            .HasDatabaseName("CIX_ItemSnapshot_CollectedAtUtc");

        // Per-item time series, and the "latest snapshot for this item" lookup the dedup check
        // performs once per item per cycle.
        entity.HasIndex(e => new { e.ItemId, e.CollectedAtUtc })
            .IsDescending(false, true)
            .IncludeProperties(e => new
            {
                e.PrimaryValue, e.SecondaryValue, e.Quantity, e.StatusText, e.HasChanged, e.RowHash
            })
            .HasDatabaseName("IX_ItemSnapshot_Item_Time");

        entity.HasIndex(e => e.CollectionRunId).HasDatabaseName("IX_ItemSnapshot_Run");
    }

    private static void ConfigureAttributeDefinition(EntityTypeBuilder<AttributeDefinition> entity)
    {
        entity.ToTable("Attribute", "core", t => t.HasCheckConstraint(
            "CK_Attribute_Type", "[DataType] IN ('Text','Number','Date','Boolean')"));

        entity.HasKey(e => e.AttributeId);
        entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UQ_Attribute_Code");

        entity.Property(e => e.Code).HasMaxLength(100).IsRequired();
        entity.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
        entity.Property(e => e.Unit).HasMaxLength(30);
        entity.Property(e => e.IsActive).HasDefaultValue(true);
        entity.Property(e => e.DataType).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
    }

    private static void ConfigureSnapshotAttribute(EntityTypeBuilder<SnapshotAttribute> entity)
    {
        // Exactly one value slot must be populated.
        entity.ToTable("ItemSnapshotAttribute", "core", t => t.HasCheckConstraint(
            "CK_ItemSnapshotAttribute_OneValue",
            "(CASE WHEN [ValueText]   IS NULL THEN 0 ELSE 1 END"
            + " + CASE WHEN [ValueNumber] IS NULL THEN 0 ELSE 1 END"
            + " + CASE WHEN [ValueDate]   IS NULL THEN 0 ELSE 1 END"
            + " + CASE WHEN [ValueBool]   IS NULL THEN 0 ELSE 1 END) = 1"));

        entity.HasKey(e => new { e.ItemSnapshotId, e.CollectedAtUtc, e.AttributeId });

        entity.Property(e => e.CollectedAtUtc).HasColumnType("datetime2(3)");
        entity.Property(e => e.ValueText).HasMaxLength(1000);
        entity.Property(e => e.ValueNumber).HasColumnType("decimal(18,4)");

        entity.HasOne(e => e.Snapshot)
            .WithMany(s => s.Attributes)
            .HasForeignKey(e => new { e.ItemSnapshotId, e.CollectedAtUtc })
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Attribute)
            .WithMany()
            .HasForeignKey(e => e.AttributeId)
            .OnDelete(DeleteBehavior.NoAction);

        entity.HasIndex(e => e.AttributeId)
            .IncludeProperties(e => new { e.ValueNumber, e.ValueText })
            .HasDatabaseName("IX_ItemSnapshotAttribute_Attribute");
    }

    private static void ConfigureRejectedRecord(EntityTypeBuilder<RejectedRecord> entity)
    {
        entity.ToTable("RejectedRecord", "core", t => t.HasCheckConstraint(
            "CK_RejectedRecord_Reason",
            "[Reason] IN ('MissingField','TypeMismatch','OutOfRange','DuplicateKey','SchemaDrift','Unknown')"));

        entity.HasKey(e => e.RejectedRecordId);

        entity.Property(e => e.SourceKey).HasMaxLength(200);
        entity.Property(e => e.RejectedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        entity.Property(e => e.ReasonDetail).HasMaxLength(1000);
        entity.Property(e => e.Reason).HasConversion<string>().HasMaxLength(30).IsUnicode(false);

        entity.HasOne(e => e.Run)
            .WithMany(r => r.RejectedRecords)
            .HasForeignKey(e => e.CollectionRunId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.CollectionRunId, e.RejectedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_RejectedRecord_Run");
    }
}
