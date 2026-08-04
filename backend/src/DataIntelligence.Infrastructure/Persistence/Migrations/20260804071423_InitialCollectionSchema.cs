using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCollectionSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "core");

            migrationBuilder.EnsureSchema(
                name: "collect");

            migrationBuilder.CreateTable(
                name: "Attribute",
                schema: "core",
                columns: table => new
                {
                    AttributeId = table.Column<short>(type: "smallint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DataType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attribute", x => x.AttributeId);
                    table.CheckConstraint("CK_Attribute_Type", "[DataType] IN ('Text','Number','Date','Boolean')");
                });

            migrationBuilder.CreateTable(
                name: "Category",
                schema: "core",
                columns: table => new
                {
                    CategoryId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParentCategoryId = table.Column<int>(type: "int", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Category", x => x.CategoryId);
                    table.CheckConstraint("CK_Category_NotSelfParent", "[ParentCategoryId] <> [CategoryId]");
                    table.ForeignKey(
                        name: "FK_Category_Category_ParentCategoryId",
                        column: x => x.ParentCategoryId,
                        principalSchema: "core",
                        principalTable: "Category",
                        principalColumn: "CategoryId");
                });

            migrationBuilder.CreateTable(
                name: "CollectionRun",
                schema: "collect",
                columns: table => new
                {
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScheduledForUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 3, nullable: false),
                    Attempt = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)1),
                    TriggerType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true, computedColumnSql: "DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc])"),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    RequestUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    HttpStatusCode = table.Column<short>(type: "smallint", nullable: true),
                    RecordsFetched = table.Column<int>(type: "int", nullable: false),
                    RecordsInserted = table.Column<int>(type: "int", nullable: false),
                    RecordsUnchanged = table.Column<int>(type: "int", nullable: false),
                    RecordsRejected = table.Column<int>(type: "int", nullable: false),
                    FailureCategory = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ErrorDetail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AlertSentAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionRun", x => x.CollectionRunId);
                    table.CheckConstraint("CK_CollectionRun_Completed", "[CompletedAtUtc] IS NULL OR [CompletedAtUtc] >= [StartedAtUtc]");
                    table.CheckConstraint("CK_CollectionRun_Failure", "[FailureCategory] IS NULL OR [FailureCategory] IN ('Unreachable','Timeout','HttpError','ParseError','LayoutChanged','Validation','Persistence','Unknown')");
                    table.CheckConstraint("CK_CollectionRun_FailureRequired", "[Status] <> 'Failed' OR [FailureCategory] IS NOT NULL");
                    table.CheckConstraint("CK_CollectionRun_Status", "[Status] IN ('Running','Succeeded','PartialSuccess','Failed','Skipped')");
                    table.CheckConstraint("CK_CollectionRun_Trigger", "[TriggerType] IN ('Scheduled','Manual','Retry','Backfill')");
                });

            migrationBuilder.CreateTable(
                name: "SourceConfig",
                schema: "collect",
                columns: table => new
                {
                    SourceConfigId = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CollectionUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CollectionIntervalMinutes = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)60),
                    RequestTimeoutSec = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)30),
                    MaxRetries = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)3),
                    UserAgent = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    RobotsTxtCheckedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceConfig", x => x.SourceConfigId);
                    table.CheckConstraint("CK_SourceConfig_Single", "[SourceConfigId] = 1");
                    table.CheckConstraint("CK_SourceConfig_Timeout", "[RequestTimeoutSec] BETWEEN 1 AND 300");
                });

            migrationBuilder.CreateTable(
                name: "Item",
                schema: "core",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: true),
                    SourceUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FirstSeenRunId = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Item", x => x.ItemId);
                    table.CheckConstraint("CK_Item_SeenOrder", "[LastSeenAtUtc] >= [FirstSeenAtUtc]");
                    table.ForeignKey(
                        name: "FK_Item_Category_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "core",
                        principalTable: "Category",
                        principalColumn: "CategoryId");
                });

            migrationBuilder.CreateTable(
                name: "RawPayload",
                schema: "collect",
                columns: table => new
                {
                    RawPayloadId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ContentHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    CompressedContent = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawPayload", x => x.RawPayloadId);
                    table.CheckConstraint("CK_RawPayload_Size", "[SizeBytes] >= 0");
                    table.ForeignKey(
                        name: "FK_RawPayload_CollectionRun_CollectionRunId",
                        column: x => x.CollectionRunId,
                        principalSchema: "collect",
                        principalTable: "CollectionRun",
                        principalColumn: "CollectionRunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RejectedRecord",
                schema: "core",
                columns: table => new
                {
                    RejectedRecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RejectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    Reason = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ReasonDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RawFragment = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RejectedRecord", x => x.RejectedRecordId);
                    table.CheckConstraint("CK_RejectedRecord_Reason", "[Reason] IN ('MissingField','TypeMismatch','OutOfRange','DuplicateKey','SchemaDrift','Unknown')");
                    table.ForeignKey(
                        name: "FK_RejectedRecord_CollectionRun_CollectionRunId",
                        column: x => x.CollectionRunId,
                        principalSchema: "collect",
                        principalTable: "CollectionRun",
                        principalColumn: "CollectionRunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ItemSnapshot",
                schema: "core",
                columns: table => new
                {
                    ItemSnapshotId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CollectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: false),
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false),
                    CollectedDateKey = table.Column<int>(type: "int", nullable: false, computedColumnSql: "CONVERT(INT, CONVERT(CHAR(8), [CollectedAtUtc], 112))", stored: true),
                    PrimaryValue = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    SecondaryValue = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: true),
                    StatusText = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CurrencyCode = table.Column<string>(type: "char(3)", nullable: true),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    RowHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    HasChanged = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemSnapshot", x => new { x.ItemSnapshotId, x.CollectedAtUtc })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_ItemSnapshot_Quantity", "[Quantity] IS NULL OR [Quantity] >= 0");
                    table.ForeignKey(
                        name: "FK_ItemSnapshot_CollectionRun_CollectionRunId",
                        column: x => x.CollectionRunId,
                        principalSchema: "collect",
                        principalTable: "CollectionRun",
                        principalColumn: "CollectionRunId");
                    table.ForeignKey(
                        name: "FK_ItemSnapshot_Item_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "core",
                        principalTable: "Item",
                        principalColumn: "ItemId");
                });

            migrationBuilder.CreateTable(
                name: "ItemSnapshotAttribute",
                schema: "core",
                columns: table => new
                {
                    ItemSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    CollectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    AttributeId = table.Column<short>(type: "smallint", nullable: false),
                    ValueText = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ValueNumber = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    ValueDate = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ValueBool = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemSnapshotAttribute", x => new { x.ItemSnapshotId, x.CollectedAtUtc, x.AttributeId });
                    table.CheckConstraint("CK_ItemSnapshotAttribute_OneValue", "(CASE WHEN [ValueText]   IS NULL THEN 0 ELSE 1 END + CASE WHEN [ValueNumber] IS NULL THEN 0 ELSE 1 END + CASE WHEN [ValueDate]   IS NULL THEN 0 ELSE 1 END + CASE WHEN [ValueBool]   IS NULL THEN 0 ELSE 1 END) = 1");
                    table.ForeignKey(
                        name: "FK_ItemSnapshotAttribute_Attribute_AttributeId",
                        column: x => x.AttributeId,
                        principalSchema: "core",
                        principalTable: "Attribute",
                        principalColumn: "AttributeId");
                    table.ForeignKey(
                        name: "FK_ItemSnapshotAttribute_ItemSnapshot_ItemSnapshotId_CollectedAtUtc",
                        columns: x => new { x.ItemSnapshotId, x.CollectedAtUtc },
                        principalSchema: "core",
                        principalTable: "ItemSnapshot",
                        principalColumns: new[] { "ItemSnapshotId", "CollectedAtUtc" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_Attribute_Code",
                schema: "core",
                table: "Attribute",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Category_Parent",
                schema: "core",
                table: "Category",
                column: "ParentCategoryId",
                filter: "[ParentCategoryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_Category_Code",
                schema: "core",
                table: "Category",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CollectionRun_Failures",
                schema: "collect",
                table: "CollectionRun",
                column: "StartedAtUtc",
                descending: new bool[0],
                filter: "[Status] IN ('Failed','PartialSuccess')")
                .Annotation("SqlServer:Include", new[] { "FailureCategory", "ErrorMessage", "AlertSentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionRun_StartedAtUtc",
                schema: "collect",
                table: "CollectionRun",
                column: "StartedAtUtc",
                descending: new bool[0])
                .Annotation("SqlServer:Include", new[] { "Status", "RecordsInserted" });

            migrationBuilder.CreateIndex(
                name: "UQ_CollectionRun_Cycle",
                schema: "collect",
                table: "CollectionRun",
                columns: new[] { "ScheduledForUtc", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Item_Category",
                schema: "core",
                table: "Item",
                columns: new[] { "CategoryId", "IsActive" })
                .Annotation("SqlServer:Include", new[] { "Title" });

            migrationBuilder.CreateIndex(
                name: "IX_Item_LastSeen",
                schema: "core",
                table: "Item",
                column: "LastSeenAtUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "UQ_Item_SourceKey",
                schema: "core",
                table: "Item",
                column: "SourceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "CIX_ItemSnapshot_CollectedAtUtc",
                schema: "core",
                table: "ItemSnapshot",
                columns: new[] { "CollectedAtUtc", "ItemId" })
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemSnapshot_Item_Time",
                schema: "core",
                table: "ItemSnapshot",
                columns: new[] { "ItemId", "CollectedAtUtc" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "PrimaryValue", "SecondaryValue", "Quantity", "StatusText", "HasChanged", "RowHash" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemSnapshot_Run",
                schema: "core",
                table: "ItemSnapshot",
                column: "CollectionRunId");

            migrationBuilder.CreateIndex(
                name: "UQ_ItemSnapshot_ItemRun",
                schema: "core",
                table: "ItemSnapshot",
                columns: new[] { "ItemId", "CollectionRunId", "CollectedAtUtc" },
                unique: true)
                .Annotation("SqlServer:Clustered", false);

            migrationBuilder.CreateIndex(
                name: "IX_ItemSnapshotAttribute_Attribute",
                schema: "core",
                table: "ItemSnapshotAttribute",
                column: "AttributeId")
                .Annotation("SqlServer:Include", new[] { "ValueNumber", "ValueText" });

            migrationBuilder.CreateIndex(
                name: "IX_RawPayload_Hash",
                schema: "collect",
                table: "RawPayload",
                columns: new[] { "ContentHash", "FetchedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RawPayload_Run",
                schema: "collect",
                table: "RawPayload",
                column: "CollectionRunId");

            migrationBuilder.CreateIndex(
                name: "IX_RejectedRecord_Run",
                schema: "core",
                table: "RejectedRecord",
                columns: new[] { "CollectionRunId", "RejectedAtUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemSnapshotAttribute",
                schema: "core");

            migrationBuilder.DropTable(
                name: "RawPayload",
                schema: "collect");

            migrationBuilder.DropTable(
                name: "RejectedRecord",
                schema: "core");

            migrationBuilder.DropTable(
                name: "SourceConfig",
                schema: "collect");

            migrationBuilder.DropTable(
                name: "Attribute",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ItemSnapshot",
                schema: "core");

            migrationBuilder.DropTable(
                name: "CollectionRun",
                schema: "collect");

            migrationBuilder.DropTable(
                name: "Item",
                schema: "core");

            migrationBuilder.DropTable(
                name: "Category",
                schema: "core");
        }
    }
}
