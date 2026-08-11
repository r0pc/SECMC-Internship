using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PakistanTimeAndDropAssistantQuery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            // DurationMs is computed from StartedAt/CompletedAt. SQL Server refuses to rename a
            // column a computed column depends on ("participates in enforced dependencies"), so it
            // is dropped and rebuilt against the new names rather than renamed alongside them.
            migrationBuilder.DropColumn(
                name: "DurationMs",
                schema: "collect",
                table: "CollectionRun");
            migrationBuilder.DropTable(
                name: "AssistantQuery",
                schema: "ai");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sofr_Superseded",
                schema: "core",
                table: "SofrDailyRate");

            migrationBuilder.DropIndex(
                name: "IX_RejectedObservation_Run",
                schema: "core",
                table: "RejectedObservation");

            migrationBuilder.DropIndex(
                name: "IX_RawPayload_Hash",
                schema: "collect",
                table: "RawPayload");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cpi_Superseded",
                schema: "core",
                table: "CpiObservation");

            migrationBuilder.DropIndex(
                name: "IX_CollectionRun_Failures",
                schema: "collect",
                table: "CollectionRun");

            migrationBuilder.DropIndex(
                name: "IX_CollectionRun_StartedAtUtc",
                schema: "collect",
                table: "CollectionRun");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CollectionRun_Completed",
                schema: "collect",
                table: "CollectionRun");

            migrationBuilder.DropIndex(
                name: "IX_AssistantSession_User",
                schema: "ai",
                table: "AssistantSession");








            migrationBuilder.RenameColumn(
                name: "RejectedAtUtc",
                schema: "core",
                table: "RejectedObservation",
                newName: "RejectedAtPkt");

            migrationBuilder.AlterColumn<DateTime>(
                name: "RejectedAtPkt",
                schema: "core",
                table: "RejectedObservation",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.RenameColumn(
                name: "FetchedAtUtc",
                schema: "collect",
                table: "RawPayload",
                newName: "FetchedAtPkt");

            migrationBuilder.AlterColumn<DateTime>(
                name: "FetchedAtPkt",
                schema: "collect",
                table: "RawPayload",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "collect",
                table: "DataSource",
                newName: "CreatedAtPkt");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtPkt",
                schema: "collect",
                table: "DataSource",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.RenameColumn(
                name: "StartedAtUtc",
                schema: "collect",
                table: "CollectionRun",
                newName: "StartedAtPkt");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAtPkt",
                schema: "collect",
                table: "CollectionRun",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.RenameColumn(
                name: "LastActivityAtUtc",
                schema: "ai",
                table: "AssistantSession",
                newName: "LastActivityAtPkt");

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastActivityAtPkt",
                schema: "ai",
                table: "AssistantSession",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.RenameColumn(
                name: "StartedAtUtc",
                schema: "ai",
                table: "AssistantSession",
                newName: "StartedAtPkt");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAtPkt",
                schema: "ai",
                table: "AssistantSession",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.RenameColumn(
                name: "SubmittedAtUtc",
                schema: "ai",
                table: "AssistantFeedback",
                newName: "SubmittedAtPkt");

            migrationBuilder.AlterColumn<DateTime>(
                name: "SubmittedAtPkt",
                schema: "ai",
                table: "AssistantFeedback",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.RenameColumn(
                name: "SupersededAtUtc",
                schema: "core",
                table: "SofrDailyRate",
                newName: "SupersededAtPkt");

            migrationBuilder.RenameColumn(
                name: "CollectedAtUtc",
                schema: "core",
                table: "SofrDailyRate",
                newName: "CollectedAtPkt");

            migrationBuilder.RenameColumn(
                name: "UpdatedAtUtc",
                schema: "collect",
                table: "DataSource",
                newName: "UpdatedAtPkt");

            migrationBuilder.RenameColumn(
                name: "RobotsTxtCheckedAtUtc",
                schema: "collect",
                table: "DataSource",
                newName: "RobotsTxtCheckedAtPkt");

            migrationBuilder.RenameColumn(
                name: "SupersededAtUtc",
                schema: "core",
                table: "CpiObservation",
                newName: "SupersededAtPkt");

            migrationBuilder.RenameColumn(
                name: "CollectedAtUtc",
                schema: "core",
                table: "CpiObservation",
                newName: "CollectedAtPkt");

            migrationBuilder.RenameColumn(
                name: "ScheduledForUtc",
                schema: "collect",
                table: "CollectionRun",
                newName: "ScheduledForPkt");

            migrationBuilder.RenameColumn(
                name: "CompletedAtUtc",
                schema: "collect",
                table: "CollectionRun",
                newName: "CompletedAtPkt");

            migrationBuilder.RenameColumn(
                name: "AlertSentAtUtc",
                schema: "collect",
                table: "CollectionRun",
                newName: "AlertSentAtPkt");









            migrationBuilder.UpdateData(
                schema: "collect",
                table: "DataSource",
                keyColumn: "DataSourceId",
                keyValue: (byte)1,
                column: "CreatedAtPkt",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.UpdateData(
                schema: "collect",
                table: "DataSource",
                keyColumn: "DataSourceId",
                keyValue: (byte)2,
                column: "CreatedAtPkt",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sofr_Superseded",
                schema: "core",
                table: "SofrDailyRate",
                sql: "([IsCurrent] = 1 AND [SupersededAtPkt] IS NULL) OR ([IsCurrent] = 0 AND [SupersededAtPkt] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_RejectedObservation_Run",
                schema: "core",
                table: "RejectedObservation",
                columns: new[] { "CollectionRunId", "RejectedAtPkt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_RawPayload_Hash",
                schema: "collect",
                table: "RawPayload",
                columns: new[] { "ContentHash", "FetchedAtPkt" },
                descending: new[] { false, true });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cpi_Superseded",
                schema: "core",
                table: "CpiObservation",
                sql: "([IsCurrent] = 1 AND [SupersededAtPkt] IS NULL) OR ([IsCurrent] = 0 AND [SupersededAtPkt] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionRun_Failures",
                schema: "collect",
                table: "CollectionRun",
                column: "StartedAtPkt",
                descending: new bool[0],
                filter: "[Status] IN ('Failed','PartialSuccess')")
                .Annotation("SqlServer:Include", new[] { "DataSourceId", "FailureCategory", "ErrorMessage", "AlertSentAtPkt" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionRun_StartedAtPkt",
                schema: "collect",
                table: "CollectionRun",
                column: "StartedAtPkt",
                descending: new bool[0])
                .Annotation("SqlServer:Include", new[] { "DataSourceId", "Status", "ObservationsInserted" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CollectionRun_Completed",
                schema: "collect",
                table: "CollectionRun",
                sql: "[CompletedAtPkt] IS NULL OR [CompletedAtPkt] >= [StartedAtPkt]");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantSession_User",
                schema: "ai",
                table: "AssistantSession",
                columns: new[] { "UserId", "StartedAtPkt" },
                descending: new[] { false, true });

            migrationBuilder.AddColumn<long>(
                name: "DurationMs",
                schema: "collect",
                table: "CollectionRun",
                type: "bigint",
                nullable: true,
                computedColumnSql: "DATEDIFF_BIG(MILLISECOND, [StartedAtPkt], [CompletedAtPkt])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

            // DurationMs is computed from StartedAt/CompletedAt. SQL Server refuses to rename a
            // column a computed column depends on ("participates in enforced dependencies"), so it
            // is dropped and rebuilt against the new names rather than renamed alongside them.
            migrationBuilder.DropColumn(
                name: "DurationMs",
                schema: "collect",
                table: "CollectionRun");

            migrationBuilder.RenameColumn(
                name: "RejectedAtPkt",
                schema: "core",
                table: "RejectedObservation",
                newName: "RejectedAtUtc");

            migrationBuilder.AlterColumn<DateTime>(
                name: "RejectedAtUtc",
                schema: "core",
                table: "RejectedObservation",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())");

            migrationBuilder.RenameColumn(
                name: "FetchedAtPkt",
                schema: "collect",
                table: "RawPayload",
                newName: "FetchedAtUtc");

            migrationBuilder.AlterColumn<DateTime>(
                name: "FetchedAtUtc",
                schema: "collect",
                table: "RawPayload",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())");

            migrationBuilder.RenameColumn(
                name: "CreatedAtPkt",
                schema: "collect",
                table: "DataSource",
                newName: "CreatedAtUtc");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                schema: "collect",
                table: "DataSource",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())");

            migrationBuilder.RenameColumn(
                name: "StartedAtPkt",
                schema: "collect",
                table: "CollectionRun",
                newName: "StartedAtUtc");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAtUtc",
                schema: "collect",
                table: "CollectionRun",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())");

            migrationBuilder.RenameColumn(
                name: "LastActivityAtPkt",
                schema: "ai",
                table: "AssistantSession",
                newName: "LastActivityAtUtc");

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastActivityAtUtc",
                schema: "ai",
                table: "AssistantSession",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())");

            migrationBuilder.RenameColumn(
                name: "StartedAtPkt",
                schema: "ai",
                table: "AssistantSession",
                newName: "StartedAtUtc");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAtUtc",
                schema: "ai",
                table: "AssistantSession",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())");

            migrationBuilder.RenameColumn(
                name: "SubmittedAtPkt",
                schema: "ai",
                table: "AssistantFeedback",
                newName: "SubmittedAtUtc");

            migrationBuilder.AlterColumn<DateTime>(
                name: "SubmittedAtUtc",
                schema: "ai",
                table: "AssistantFeedback",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldPrecision: 3,
                oldDefaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())");
            migrationBuilder.DropCheckConstraint(
                name: "CK_Sofr_Superseded",
                schema: "core",
                table: "SofrDailyRate");

            migrationBuilder.DropIndex(
                name: "IX_RejectedObservation_Run",
                schema: "core",
                table: "RejectedObservation");

            migrationBuilder.DropIndex(
                name: "IX_RawPayload_Hash",
                schema: "collect",
                table: "RawPayload");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cpi_Superseded",
                schema: "core",
                table: "CpiObservation");

            migrationBuilder.DropIndex(
                name: "IX_CollectionRun_Failures",
                schema: "collect",
                table: "CollectionRun");

            migrationBuilder.DropIndex(
                name: "IX_CollectionRun_StartedAtPkt",
                schema: "collect",
                table: "CollectionRun");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CollectionRun_Completed",
                schema: "collect",
                table: "CollectionRun");

            migrationBuilder.DropIndex(
                name: "IX_AssistantSession_User",
                schema: "ai",
                table: "AssistantSession");








            migrationBuilder.RenameColumn(
                name: "SupersededAtPkt",
                schema: "core",
                table: "SofrDailyRate",
                newName: "SupersededAtUtc");

            migrationBuilder.RenameColumn(
                name: "CollectedAtPkt",
                schema: "core",
                table: "SofrDailyRate",
                newName: "CollectedAtUtc");

            migrationBuilder.RenameColumn(
                name: "UpdatedAtPkt",
                schema: "collect",
                table: "DataSource",
                newName: "UpdatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "RobotsTxtCheckedAtPkt",
                schema: "collect",
                table: "DataSource",
                newName: "RobotsTxtCheckedAtUtc");

            migrationBuilder.RenameColumn(
                name: "SupersededAtPkt",
                schema: "core",
                table: "CpiObservation",
                newName: "SupersededAtUtc");

            migrationBuilder.RenameColumn(
                name: "CollectedAtPkt",
                schema: "core",
                table: "CpiObservation",
                newName: "CollectedAtUtc");

            migrationBuilder.RenameColumn(
                name: "ScheduledForPkt",
                schema: "collect",
                table: "CollectionRun",
                newName: "ScheduledForUtc");

            migrationBuilder.RenameColumn(
                name: "CompletedAtPkt",
                schema: "collect",
                table: "CollectionRun",
                newName: "CompletedAtUtc");

            migrationBuilder.RenameColumn(
                name: "AlertSentAtPkt",
                schema: "collect",
                table: "CollectionRun",
                newName: "AlertSentAtUtc");









            migrationBuilder.CreateTable(
                name: "AssistantQuery",
                schema: "ai",
                columns: table => new
                {
                    AssistantQueryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnswerText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AskedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ClientIpHash = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    CompletionTokens = table.Column<int>(type: "int", nullable: true),
                    ExecutionError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ExecutionMs = table.Column<int>(type: "int", nullable: true),
                    ExecutionStatus = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    Explanation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    GeneratedSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PromptTokens = table.Column<int>(type: "int", nullable: true),
                    QuestionText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ResultRowCount = table.Column<int>(type: "int", nullable: true),
                    SqlParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TotalLatencyMs = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    ValidationDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ValidationOutcome = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false, defaultValue: "Pending"),
                    VisualizationJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WasExecuted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantQuery", x => x.AssistantQueryId);
                    table.CheckConstraint("CK_AssistantQuery_Execution", "[ExecutionStatus] IS NULL OR [ExecutionStatus] IN ('Succeeded','Failed','Timeout','Cancelled')");
                    table.CheckConstraint("CK_AssistantQuery_NoUnvalidatedRun", "[WasExecuted] = 0 OR [ValidationOutcome] = 'Approved'");
                    table.CheckConstraint("CK_AssistantQuery_Validation", "[ValidationOutcome] IN ('Pending','Approved','RejectedNotSelect','RejectedForbiddenObject','RejectedSyntax','RejectedComplexity','RejectedNoSql','NotADataQuestion','RejectedUnreadableResponse')");
                    table.ForeignKey(
                        name: "FK_AssistantQuery_AssistantSession_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "ai",
                        principalTable: "AssistantSession",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                schema: "collect",
                table: "DataSource",
                keyColumn: "DataSourceId",
                keyValue: (byte)1,
                column: "CreatedAtUtc",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.UpdateData(
                schema: "collect",
                table: "DataSource",
                keyColumn: "DataSourceId",
                keyValue: (byte)2,
                column: "CreatedAtUtc",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sofr_Superseded",
                schema: "core",
                table: "SofrDailyRate",
                sql: "([IsCurrent] = 1 AND [SupersededAtUtc] IS NULL) OR ([IsCurrent] = 0 AND [SupersededAtUtc] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_RejectedObservation_Run",
                schema: "core",
                table: "RejectedObservation",
                columns: new[] { "CollectionRunId", "RejectedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_RawPayload_Hash",
                schema: "collect",
                table: "RawPayload",
                columns: new[] { "ContentHash", "FetchedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cpi_Superseded",
                schema: "core",
                table: "CpiObservation",
                sql: "([IsCurrent] = 1 AND [SupersededAtUtc] IS NULL) OR ([IsCurrent] = 0 AND [SupersededAtUtc] IS NOT NULL)");

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

            migrationBuilder.AddCheckConstraint(
                name: "CK_CollectionRun_Completed",
                schema: "collect",
                table: "CollectionRun",
                sql: "[CompletedAtUtc] IS NULL OR [CompletedAtUtc] >= [StartedAtUtc]");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantSession_User",
                schema: "ai",
                table: "AssistantSession",
                columns: new[] { "UserId", "StartedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantQuery_AskedAtUtc",
                schema: "ai",
                table: "AssistantQuery",
                column: "AskedAtUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantQuery_Rejected",
                schema: "ai",
                table: "AssistantQuery",
                column: "AskedAtUtc",
                descending: new bool[0],
                filter: "[ValidationOutcome] <> 'Approved' AND [ValidationOutcome] <> 'Pending' AND [ValidationOutcome] <> 'NotADataQuestion'")
                .Annotation("SqlServer:Include", new[] { "QuestionText", "ValidationOutcome", "ValidationDetail" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantQuery_Session",
                schema: "ai",
                table: "AssistantQuery",
                columns: new[] { "SessionId", "AskedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantQuery_User",
                schema: "ai",
                table: "AssistantQuery",
                columns: new[] { "UserId", "AskedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.AddColumn<long>(
                name: "DurationMs",
                schema: "collect",
                table: "CollectionRun",
                type: "bigint",
                nullable: true,
                computedColumnSql: "DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc])");
        }
    }
}
