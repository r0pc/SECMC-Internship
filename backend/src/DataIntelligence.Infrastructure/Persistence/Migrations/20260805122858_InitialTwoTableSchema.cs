using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTwoTableSchema : Migration
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
                    AccessMethod = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, defaultValue: "RestApi"),
                    HttpMethod = table.Column<string>(type: "varchar(6)", unicode: false, maxLength: 6, nullable: false, defaultValue: "GET"),
                    RequiresApiKey = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
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
                name: "CollectionRun",
                schema: "collect",
                columns: table => new
                {
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<byte>(type: "tinyint", nullable: false),
                    ScheduledForUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 3, nullable: false),
                    Attempt = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)1),
                    TriggerType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, defaultValue: "Scheduled"),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true, computedColumnSql: "DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc])"),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, defaultValue: "Running"),
                    RequestUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    HttpStatusCode = table.Column<short>(type: "smallint", nullable: true),
                    ObservationsFetched = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ObservationsInserted = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ObservationsRevised = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ObservationsUnchanged = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ObservationsRejected = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
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
                name: "CpiObservation",
                schema: "core",
                columns: table => new
                {
                    CpiObservationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SeriesCode = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, defaultValue: "CUUR0000SA0"),
                    ReferenceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReferenceYear = table.Column<short>(type: "smallint", nullable: false),
                    PeriodCode = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    PeriodType = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    IndexValue = table.Column<decimal>(type: "decimal(12,3)", nullable: false),
                    Footnotes = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    RevisionNumber = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SupersededAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false),
                    CollectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    RowHash = table.Column<byte[]>(type: "binary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CpiObservation", x => x.CpiObservationId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_Cpi_IndexValue", "[IndexValue] > 0");
                    table.CheckConstraint("CK_Cpi_PeriodCode", "[PeriodCode] IN ('M01','M02','M03','M04','M05','M06','M07','M08','M09','M10','M11','M12','M13','S01','S02')");
                    table.CheckConstraint("CK_Cpi_PeriodType", "([PeriodCode] BETWEEN 'M01' AND 'M12' AND [PeriodType] = 'Month') OR ([PeriodCode] = 'M13' AND [PeriodType] = 'Annual') OR ([PeriodCode] IN ('S01','S02') AND [PeriodType] = 'Semiannual')");
                    table.CheckConstraint("CK_Cpi_ReferenceDate", "DAY([ReferenceDate]) = 1 AND YEAR([ReferenceDate]) = [ReferenceYear] AND MONTH([ReferenceDate]) = CASE WHEN [PeriodCode] BETWEEN 'M01' AND 'M12' THEN CONVERT(INT, SUBSTRING([PeriodCode], 2, 2)) WHEN [PeriodCode] = 'S02' THEN 7 ELSE 1 END");
                    table.CheckConstraint("CK_Cpi_ReferenceYear", "[ReferenceYear] BETWEEN 1913 AND 2200");
                    table.CheckConstraint("CK_Cpi_Revision", "[RevisionNumber] >= 0");
                    table.CheckConstraint("CK_Cpi_SeriesCode", "[SeriesCode] = 'CUUR0000SA0'");
                    table.CheckConstraint("CK_Cpi_Superseded", "([IsCurrent] = 1 AND [SupersededAtUtc] IS NULL) OR ([IsCurrent] = 0 AND [SupersededAtUtc] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_CpiObservation_CollectionRun_CollectionRunId",
                        column: x => x.CollectionRunId,
                        principalSchema: "collect",
                        principalTable: "CollectionRun",
                        principalColumn: "CollectionRunId");
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
                name: "SofrDailyRate",
                schema: "core",
                columns: table => new
                {
                    SofrDailyRateId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RateType = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false, defaultValue: "SOFR"),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RatePercent = table.Column<decimal>(type: "decimal(9,5)", nullable: false),
                    Percentile1Percent = table.Column<decimal>(type: "decimal(9,5)", nullable: true),
                    Percentile25Percent = table.Column<decimal>(type: "decimal(9,5)", nullable: true),
                    Percentile75Percent = table.Column<decimal>(type: "decimal(9,5)", nullable: true),
                    Percentile99Percent = table.Column<decimal>(type: "decimal(9,5)", nullable: true),
                    VolumeUsdBillions = table.Column<decimal>(type: "decimal(12,3)", nullable: true),
                    Average30DayPercent = table.Column<decimal>(type: "decimal(9,5)", nullable: true),
                    Average90DayPercent = table.Column<decimal>(type: "decimal(9,5)", nullable: true),
                    Average180DayPercent = table.Column<decimal>(type: "decimal(9,5)", nullable: true),
                    SofrIndexValue = table.Column<decimal>(type: "decimal(20,8)", nullable: true),
                    RevisionIndicator = table.Column<string>(type: "char(1)", unicode: false, nullable: true),
                    FootnoteId = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    RevisionNumber = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SupersededAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CollectionRunId = table.Column<long>(type: "bigint", nullable: false),
                    CollectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    RowHash = table.Column<byte[]>(type: "binary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SofrDailyRate", x => x.SofrDailyRateId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_Sofr_PercentileOrder", "([Percentile1Percent] IS NULL OR [Percentile25Percent] IS NULL OR [Percentile1Percent] <= [Percentile25Percent]) AND ([Percentile25Percent] IS NULL OR [Percentile75Percent] IS NULL OR [Percentile25Percent] <= [Percentile75Percent]) AND ([Percentile75Percent] IS NULL OR [Percentile99Percent] IS NULL OR [Percentile75Percent] <= [Percentile99Percent])");
                    table.CheckConstraint("CK_Sofr_RateRange", "[RatePercent] BETWEEN -5 AND 25");
                    table.CheckConstraint("CK_Sofr_RateType", "[RateType] = 'SOFR'");
                    table.CheckConstraint("CK_Sofr_Revision", "[RevisionNumber] >= 0");
                    table.CheckConstraint("CK_Sofr_RevisionIndicator", "[RevisionIndicator] IS NULL OR [RevisionIndicator] IN ('Y','N')");
                    table.CheckConstraint("CK_Sofr_Superseded", "([IsCurrent] = 1 AND [SupersededAtUtc] IS NULL) OR ([IsCurrent] = 0 AND [SupersededAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_Sofr_Volume", "[VolumeUsdBillions] IS NULL OR [VolumeUsdBillions] >= 0");
                    table.ForeignKey(
                        name: "FK_SofrDailyRate_CollectionRun_CollectionRunId",
                        column: x => x.CollectionRunId,
                        principalSchema: "collect",
                        principalTable: "CollectionRun",
                        principalColumn: "CollectionRunId");
                });

            migrationBuilder.InsertData(
                schema: "collect",
                table: "DataSource",
                columns: new[] { "DataSourceId", "ApiEndpoint", "Code", "CollectionIntervalMinutes", "CreatedAtUtc", "HttpMethod", "IsEnabled", "LandingPageUrl", "MaxRetries", "Name", "PublicationCadence", "Publisher", "RequestTimeoutSec", "RobotsTxtCheckedAtUtc", "TermsOfUseUrl", "UpdatedAtUtc", "UserAgent" },
                values: new object[,]
                {
                    { (byte)1, "https://api.bls.gov/publicAPI/v2/timeseries/data/", "BLS_CPI", (short)60, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "POST", true, "https://www.bls.gov/data/home.htm", (byte)3, "US Consumer Price Index (CUUR0000SA0)", "Monthly", "U.S. Bureau of Labor Statistics", (short)30, null, "https://www.bls.gov/developers/api_faqs.htm", null, null },
                    { (byte)2, "https://markets.newyorkfed.org/api/rates/secured/sofr/search.json", "NYFED_SOFR", (short)60, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "GET", true, "https://www.newyorkfed.org/markets/reference-rates/sofr", (byte)3, "Secured Overnight Financing Rate", "BusinessDaily", "Federal Reserve Bank of New York", (short)30, null, "https://www.newyorkfed.org/markets/reference-rates/terms-of-use-for-selected-rate-data", null, null }
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
                name: "IX_CpiObservation_Monthly",
                schema: "core",
                table: "CpiObservation",
                columns: new[] { "PeriodType", "ReferenceDate" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "IndexValue", "RowHash", "IsCurrent", "RevisionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_CpiObservation_Run",
                schema: "core",
                table: "CpiObservation",
                column: "CollectionRunId");

            migrationBuilder.CreateIndex(
                name: "UQ_Cpi_Vintage",
                schema: "core",
                table: "CpiObservation",
                columns: new[] { "ReferenceDate", "PeriodCode", "RevisionNumber" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UQ_CpiObservation_Current",
                schema: "core",
                table: "CpiObservation",
                columns: new[] { "ReferenceYear", "PeriodCode" },
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "UQ_DataSource_Code",
                schema: "collect",
                table: "DataSource",
                column: "Code",
                unique: true);

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
                name: "IX_SofrDailyRate_Run",
                schema: "core",
                table: "SofrDailyRate",
                column: "CollectionRunId");

            migrationBuilder.CreateIndex(
                name: "UQ_Sofr_Vintage",
                schema: "core",
                table: "SofrDailyRate",
                columns: new[] { "EffectiveDate", "RevisionNumber" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UQ_SofrDailyRate_Current",
                schema: "core",
                table: "SofrDailyRate",
                column: "EffectiveDate",
                unique: true,
                filter: "[IsCurrent] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CpiObservation",
                schema: "core");

            migrationBuilder.DropTable(
                name: "RawPayload",
                schema: "collect");

            migrationBuilder.DropTable(
                name: "RejectedObservation",
                schema: "core");

            migrationBuilder.DropTable(
                name: "SofrDailyRate",
                schema: "core");

            migrationBuilder.DropTable(
                name: "CollectionRun",
                schema: "collect");

            migrationBuilder.DropTable(
                name: "DataSource",
                schema: "collect");
        }
    }
}
