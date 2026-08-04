using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTimeSeriesSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "collect");

            migrationBuilder.EnsureSchema(
                name: "core");

            migrationBuilder.CreateTable(
                name: "DataSource",
                schema: "collect",
                columns: table => new
                {
                    DataSourceId = table.Column<byte>(type: "tinyint", nullable: false),
                    Code = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Publisher = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LandingPageUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ApiEndpoint = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AccessMethod = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    HttpMethod = table.Column<string>(type: "varchar(6)", unicode: false, maxLength: 6, nullable: false, defaultValue: "GET"),
                    RequiresApiKey = table.Column<bool>(type: "bit", nullable: false),
                    PublicationCadence = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CollectionIntervalMinutes = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)60),
                    RequestTimeoutSec = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)30),
                    MaxRetries = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)3),
                    UserAgent = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    TermsOfUseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RobotsTxtCheckedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSource", x => x.DataSourceId);
                    table.CheckConstraint("CK_DataSource_Access", "[AccessMethod] IN ('RestApi','Html','Csv')");
                    table.CheckConstraint("CK_DataSource_Cadence", "[PublicationCadence] IN ('BusinessDaily','Daily','Weekly','Monthly','Quarterly','Annual','Irregular')");
                    table.CheckConstraint("CK_DataSource_Interval", "[CollectionIntervalMinutes] BETWEEN 1 AND 1440");
                    table.CheckConstraint("CK_DataSource_Method", "[HttpMethod] IN ('GET','POST')");
                    table.CheckConstraint("CK_DataSource_Timeout", "[RequestTimeoutSec] BETWEEN 1 AND 300");
                });

            migrationBuilder.CreateTable(
                name: "SeriesCategory",
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
                    table.PrimaryKey("PK_SeriesCategory", x => x.CategoryId);
                    table.CheckConstraint("CK_SeriesCategory_NotSelfParent", "[ParentCategoryId] <> [CategoryId]");
                    table.ForeignKey(
                        name: "FK_SeriesCategory_SeriesCategory_ParentCategoryId",
                        column: x => x.ParentCategoryId,
                        principalSchema: "core",
                        principalTable: "SeriesCategory",
                        principalColumn: "CategoryId");
                });

            migrationBuilder.CreateTable(
                name: "CollectionRun",
                schema: "collect",
                columns: table => new
                {
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<byte>(type: "tinyint", nullable: false),
                    ScheduledForUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 3, nullable: false),
                    Attempt = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)1),
                    TriggerType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true, computedColumnSql: "DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc])"),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    RequestUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    HttpStatusCode = table.Column<short>(type: "smallint", nullable: true),
                    ObservationsFetched = table.Column<int>(type: "int", nullable: false),
                    ObservationsInserted = table.Column<int>(type: "int", nullable: false),
                    ObservationsRevised = table.Column<int>(type: "int", nullable: false),
                    ObservationsUnchanged = table.Column<int>(type: "int", nullable: false),
                    ObservationsRejected = table.Column<int>(type: "int", nullable: false),
                    FailureCategory = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ErrorDetail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AlertSentAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionRun", x => x.CollectionRunId);
                    table.CheckConstraint("CK_CollectionRun_Completed", "[CompletedAtUtc] IS NULL OR [CompletedAtUtc] >= [StartedAtUtc]");
                    table.CheckConstraint("CK_CollectionRun_Failure", "[FailureCategory] IS NULL OR [FailureCategory] IN ('Unreachable','Timeout','HttpError','RateLimited','ParseError','SchemaChanged','Validation','Persistence','Unknown')");
                    table.CheckConstraint("CK_CollectionRun_FailureRequired", "[Status] <> 'Failed' OR [FailureCategory] IS NOT NULL");
                    table.CheckConstraint("CK_CollectionRun_Status", "[Status] IN ('Running','Succeeded','PartialSuccess','Failed','Skipped')");
                    table.CheckConstraint("CK_CollectionRun_Trigger", "[TriggerType] IN ('Scheduled','Manual','Retry','Backfill')");
                    table.ForeignKey(
                        name: "FK_CollectionRun_DataSource_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "collect",
                        principalTable: "DataSource",
                        principalColumn: "DataSourceId");
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
                name: "RejectedObservation",
                schema: "core",
                columns: table => new
                {
                    RejectedObservationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SeriesCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReferenceDateText = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RejectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    Reason = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ReasonDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RawFragment = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RejectedObservation", x => x.RejectedObservationId);
                    table.CheckConstraint("CK_RejectedObservation_Reason", "[Reason] IN ('MissingField','TypeMismatch','OutOfRange','UnknownSeries','DuplicatePeriod','UnparseablePeriod','SchemaDrift','Unknown')");
                    table.ForeignKey(
                        name: "FK_RejectedObservation_CollectionRun_CollectionRunId",
                        column: x => x.CollectionRunId,
                        principalSchema: "collect",
                        principalTable: "CollectionRun",
                        principalColumn: "CollectionRunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Series",
                schema: "core",
                columns: table => new
                {
                    SeriesId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<byte>(type: "tinyint", nullable: false),
                    SeriesCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsSourceAssignedCode = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SourceFieldPath = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DecimalPlaces = table.Column<byte>(type: "tinyint", nullable: true),
                    Frequency = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    SeasonalAdjustment = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FirstSeenRunId = table.Column<long>(type: "bigint", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.SeriesId);
                    table.CheckConstraint("CK_Series_FieldPath", "[IsSourceAssignedCode] = 1 OR [SourceFieldPath] IS NOT NULL");
                    table.CheckConstraint("CK_Series_Frequency", "[Frequency] IN ('BusinessDaily','Daily','Weekly','Monthly','Quarterly','Semiannual','Annual')");
                    table.CheckConstraint("CK_Series_Seasonal", "[SeasonalAdjustment] IN ('SeasonallyAdjusted','NotSeasonallyAdjusted','NotApplicable')");
                    table.CheckConstraint("CK_Series_SeenOrder", "[LastSeenAtUtc] IS NULL OR [FirstSeenAtUtc] IS NULL OR [LastSeenAtUtc] >= [FirstSeenAtUtc]");
                    table.ForeignKey(
                        name: "FK_Series_CollectionRun_FirstSeenRunId",
                        column: x => x.FirstSeenRunId,
                        principalSchema: "collect",
                        principalTable: "CollectionRun",
                        principalColumn: "CollectionRunId");
                    table.ForeignKey(
                        name: "FK_Series_DataSource_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "collect",
                        principalTable: "DataSource",
                        principalColumn: "DataSourceId");
                    table.ForeignKey(
                        name: "FK_Series_SeriesCategory_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "core",
                        principalTable: "SeriesCategory",
                        principalColumn: "CategoryId");
                });

            migrationBuilder.CreateTable(
                name: "Observation",
                schema: "core",
                columns: table => new
                {
                    ObservationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReferenceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SeriesId = table.Column<int>(type: "int", nullable: false),
                    PeriodType = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    SourcePeriodCode = table.Column<string>(type: "varchar(6)", unicode: false, maxLength: 6, nullable: true),
                    RevisionNumber = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SupersededAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    Value = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    SourceAnnotation = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false),
                    CollectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ReferenceDateKey = table.Column<int>(type: "int", nullable: false, computedColumnSql: "CONVERT(INT, CONVERT(CHAR(8), [ReferenceDate], 112))", stored: true),
                    RowHash = table.Column<byte[]>(type: "binary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Observation", x => new { x.ObservationId, x.ReferenceDate })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_Observation_PeriodType", "[PeriodType] IN ('Day','Week','Month','Quarter','Semiannual','Annual')");
                    table.CheckConstraint("CK_Observation_Revision", "[RevisionNumber] >= 0");
                    table.CheckConstraint("CK_Observation_Superseded", "([IsCurrent] = 1 AND [SupersededAtUtc] IS NULL) OR ([IsCurrent] = 0 AND [SupersededAtUtc] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Observation_CollectionRun_CollectionRunId",
                        column: x => x.CollectionRunId,
                        principalSchema: "collect",
                        principalTable: "CollectionRun",
                        principalColumn: "CollectionRunId");
                    table.ForeignKey(
                        name: "FK_Observation_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalSchema: "core",
                        principalTable: "Series",
                        principalColumn: "SeriesId");
                });

            migrationBuilder.InsertData(
                schema: "collect",
                table: "DataSource",
                columns: new[] { "DataSourceId", "AccessMethod", "ApiEndpoint", "Code", "CollectionIntervalMinutes", "CreatedAtUtc", "HttpMethod", "IsEnabled", "LandingPageUrl", "MaxRetries", "Name", "PublicationCadence", "Publisher", "RequestTimeoutSec", "RequiresApiKey", "RobotsTxtCheckedAtUtc", "TermsOfUseUrl", "UpdatedAtUtc", "UserAgent" },
                values: new object[,]
                {
                    { (byte)1, "RestApi", "https://api.bls.gov/publicAPI/v2/timeseries/data/", "BLS_CPI", (short)60, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "POST", true, "https://www.bls.gov/data/home.htm", (byte)3, "US Consumer Price Index", "Monthly", "U.S. Bureau of Labor Statistics", (short)30, false, null, "https://www.bls.gov/developers/api_faqs.htm", null, null },
                    { (byte)2, "RestApi", "https://markets.newyorkfed.org/api/rates/secured/sofr/last/10.json", "NYFED_SOFR", (short)60, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "GET", true, "https://www.newyorkfed.org/markets/reference-rates/sofr", (byte)3, "Secured Overnight Financing Rate", "BusinessDaily", "Federal Reserve Bank of New York", (short)30, false, null, "https://www.newyorkfed.org/markets/reference-rates/terms-of-use-for-selected-rate-data", null, null }
                });

            migrationBuilder.InsertData(
                schema: "core",
                table: "SeriesCategory",
                columns: new[] { "CategoryId", "Code", "CreatedAtUtc", "DisplayName", "ParentCategoryId", "SortOrder" },
                values: new object[,]
                {
                    { 1, "cpi-headline", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "CPI — All items", null, (short)10 },
                    { 2, "cpi-core", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "CPI — All items less food and energy", null, (short)20 },
                    { 3, "sofr-rate", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SOFR — Rate", null, (short)30 },
                    { 4, "sofr-liquidity", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SOFR — Volume and distribution", null, (short)40 }
                });

            migrationBuilder.InsertData(
                schema: "core",
                table: "Series",
                columns: new[] { "SeriesId", "CategoryId", "DataSourceId", "DecimalPlaces", "FirstSeenAtUtc", "FirstSeenRunId", "Frequency", "IsActive", "IsSourceAssignedCode", "LastSeenAtUtc", "SeasonalAdjustment", "SeriesCode", "SourceFieldPath", "SourceUrl", "Title", "Unit" },
                values: new object[,]
                {
                    { 1, 1, (byte)1, (byte)3, null, null, "Monthly", true, true, null, "NotSeasonallyAdjusted", "CUUR0000SA0", null, "https://www.bls.gov/cpi/", "CPI-U, All items, US city average, not seasonally adjusted", "Index 1982-84=100" },
                    { 2, 1, (byte)1, (byte)3, null, null, "Monthly", true, true, null, "SeasonallyAdjusted", "CUSR0000SA0", null, "https://www.bls.gov/cpi/", "CPI-U, All items, US city average, seasonally adjusted", "Index 1982-84=100" },
                    { 3, 2, (byte)1, (byte)3, null, null, "Monthly", true, true, null, "NotSeasonallyAdjusted", "CUUR0000SA0L1E", null, "https://www.bls.gov/cpi/", "CPI-U, All items less food and energy, not seasonally adjusted", "Index 1982-84=100" },
                    { 4, 2, (byte)1, (byte)3, null, null, "Monthly", true, true, null, "SeasonallyAdjusted", "CUSR0000SA0L1E", null, "https://www.bls.gov/cpi/", "CPI-U, All items less food and energy, seasonally adjusted", "Index 1982-84=100" }
                });

            migrationBuilder.InsertData(
                schema: "core",
                table: "Series",
                columns: new[] { "SeriesId", "CategoryId", "DataSourceId", "DecimalPlaces", "FirstSeenAtUtc", "FirstSeenRunId", "Frequency", "IsActive", "LastSeenAtUtc", "SeasonalAdjustment", "SeriesCode", "SourceFieldPath", "SourceUrl", "Title", "Unit" },
                values: new object[,]
                {
                    { 5, 3, (byte)2, (byte)2, null, null, "BusinessDaily", true, null, "NotApplicable", "SOFR", "percentRate", "https://www.newyorkfed.org/markets/reference-rates/sofr", "SOFR, overnight rate", "Percent per annum" },
                    { 6, 4, (byte)2, (byte)0, null, null, "BusinessDaily", true, null, "NotApplicable", "SOFR_VOL", "volumeInBillions", "https://www.newyorkfed.org/markets/reference-rates/sofr", "SOFR, transaction volume", "USD billions" },
                    { 7, 4, (byte)2, (byte)2, null, null, "BusinessDaily", true, null, "NotApplicable", "SOFR_P1", "percentPercentile1", "https://www.newyorkfed.org/markets/reference-rates/sofr", "SOFR, 1st percentile", "Percent per annum" },
                    { 8, 4, (byte)2, (byte)2, null, null, "BusinessDaily", true, null, "NotApplicable", "SOFR_P25", "percentPercentile25", "https://www.newyorkfed.org/markets/reference-rates/sofr", "SOFR, 25th percentile", "Percent per annum" },
                    { 9, 4, (byte)2, (byte)2, null, null, "BusinessDaily", true, null, "NotApplicable", "SOFR_P75", "percentPercentile75", "https://www.newyorkfed.org/markets/reference-rates/sofr", "SOFR, 75th percentile", "Percent per annum" },
                    { 10, 4, (byte)2, (byte)2, null, null, "BusinessDaily", true, null, "NotApplicable", "SOFR_P99", "percentPercentile99", "https://www.newyorkfed.org/markets/reference-rates/sofr", "SOFR, 99th percentile", "Percent per annum" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionRun_Failures",
                schema: "collect",
                table: "CollectionRun",
                column: "StartedAtUtc",
                descending: new bool[0],
                filter: "[Status] IN ('Failed','PartialSuccess')")
                .Annotation("SqlServer:Include", new[] { "DataSourceId", "FailureCategory", "ErrorMessage", "AlertSentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionRun_StartedAtUtc",
                schema: "collect",
                table: "CollectionRun",
                column: "StartedAtUtc",
                descending: new bool[0])
                .Annotation("SqlServer:Include", new[] { "DataSourceId", "Status", "ObservationsInserted" });

            migrationBuilder.CreateIndex(
                name: "UQ_CollectionRun_Cycle",
                schema: "collect",
                table: "CollectionRun",
                columns: new[] { "DataSourceId", "ScheduledForUtc", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_DataSource_Code",
                schema: "collect",
                table: "DataSource",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "CIX_Observation_Reference",
                schema: "core",
                table: "Observation",
                columns: new[] { "ReferenceDate", "SeriesId" })
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Observation_Run",
                schema: "core",
                table: "Observation",
                column: "CollectionRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Observation_Series_Reference",
                schema: "core",
                table: "Observation",
                columns: new[] { "SeriesId", "ReferenceDate" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "Value", "RowHash", "IsCurrent", "PeriodType" });

            migrationBuilder.CreateIndex(
                name: "UQ_Observation_Current",
                schema: "core",
                table: "Observation",
                columns: new[] { "SeriesId", "ReferenceDate" },
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "UQ_Observation_Vintage",
                schema: "core",
                table: "Observation",
                columns: new[] { "SeriesId", "ReferenceDate", "RevisionNumber" },
                unique: true)
                .Annotation("SqlServer:Clustered", false);

            migrationBuilder.CreateIndex(
                name: "IX_RawPayload_Hash",
                schema: "collect",
                table: "RawPayload",
                columns: new[] { "ContentHash", "FetchedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_RawPayload_Run",
                schema: "collect",
                table: "RawPayload",
                column: "CollectionRunId");

            migrationBuilder.CreateIndex(
                name: "IX_RejectedObservation_Run",
                schema: "core",
                table: "RejectedObservation",
                columns: new[] { "CollectionRunId", "RejectedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Series_Category",
                schema: "core",
                table: "Series",
                columns: new[] { "CategoryId", "IsActive" })
                .Annotation("SqlServer:Include", new[] { "Title" });

            migrationBuilder.CreateIndex(
                name: "IX_Series_FirstSeenRunId",
                schema: "core",
                table: "Series",
                column: "FirstSeenRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Series_Source",
                schema: "core",
                table: "Series",
                columns: new[] { "DataSourceId", "IsActive" })
                .Annotation("SqlServer:Include", new[] { "SeriesCode", "Title" });

            migrationBuilder.CreateIndex(
                name: "UQ_Series_Code",
                schema: "core",
                table: "Series",
                columns: new[] { "DataSourceId", "SeriesCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeriesCategory_Parent",
                schema: "core",
                table: "SeriesCategory",
                column: "ParentCategoryId",
                filter: "[ParentCategoryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_SeriesCategory_Code",
                schema: "core",
                table: "SeriesCategory",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Observation",
                schema: "core");

            migrationBuilder.DropTable(
                name: "RawPayload",
                schema: "collect");

            migrationBuilder.DropTable(
                name: "RejectedObservation",
                schema: "core");

            migrationBuilder.DropTable(
                name: "Series",
                schema: "core");

            migrationBuilder.DropTable(
                name: "CollectionRun",
                schema: "collect");

            migrationBuilder.DropTable(
                name: "SeriesCategory",
                schema: "core");

            migrationBuilder.DropTable(
                name: "DataSource",
                schema: "collect");
        }
    }
}
