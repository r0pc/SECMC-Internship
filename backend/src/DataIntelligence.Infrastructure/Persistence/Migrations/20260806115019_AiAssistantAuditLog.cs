using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The AI assistant's audit log — <c>ai.AssistantSession</c>, <c>ai.AssistantQuery</c> and
    /// <c>ai.AssistantFeedback</c> (NFR Auditability).
    /// </summary>
    /// <remarks>
    /// One deliberate difference from <c>docs/database-schema.sql</c>: the design gives
    /// <c>AssistantSession.UserId</c> and <c>AssistantQuery.UserId</c> a foreign key to
    /// <c>sec.AppUser</c>, and this migration does not create it. That table arrives with
    /// authentication (FR-9), and a foreign key cannot reference a table that does not exist yet.
    /// The columns are created with the right type and indexes, so adding the constraint later is
    /// a one-line migration. Until then the endpoint records a placeholder user id — see the TODO
    /// in <c>AssistantEndpoints</c>.
    /// </remarks>
    public partial class AiAssistantAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.CreateTable(
                name: "AssistantSession",
                schema: "ai",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    LastActivityAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantSession", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "AssistantQuery",
                schema: "ai",
                columns: table => new
                {
                    AssistantQueryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    AskedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    QuestionText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    GeneratedSql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SqlParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidationOutcome = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, defaultValue: "Pending"),
                    ValidationDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    WasExecuted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ExecutionStatus = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    ExecutionMs = table.Column<int>(type: "int", nullable: true),
                    ResultRowCount = table.Column<int>(type: "int", nullable: true),
                    ExecutionError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AnswerText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    VisualizationJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PromptTokens = table.Column<int>(type: "int", nullable: true),
                    CompletionTokens = table.Column<int>(type: "int", nullable: true),
                    TotalLatencyMs = table.Column<int>(type: "int", nullable: true),
                    ClientIpHash = table.Column<byte[]>(type: "binary(32)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantQuery", x => x.AssistantQueryId);
                    table.CheckConstraint("CK_AssistantQuery_Execution", "[ExecutionStatus] IS NULL OR [ExecutionStatus] IN ('Succeeded','Failed','Timeout','Cancelled')");
                    table.CheckConstraint("CK_AssistantQuery_NoUnvalidatedRun", "[WasExecuted] = 0 OR [ValidationOutcome] = 'Approved'");
                    table.CheckConstraint("CK_AssistantQuery_Validation", "[ValidationOutcome] IN ('Pending','Approved','RejectedNotSelect','RejectedForbiddenObject','RejectedSyntax','RejectedComplexity','RejectedNoSql')");
                    table.ForeignKey(
                        name: "FK_AssistantQuery_AssistantSession_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "ai",
                        principalTable: "AssistantSession",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssistantFeedback",
                schema: "ai",
                columns: table => new
                {
                    AssistantQueryId = table.Column<long>(type: "bigint", nullable: false),
                    IsHelpful = table.Column<bool>(type: "bit", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantFeedback", x => x.AssistantQueryId);
                    table.ForeignKey(
                        name: "FK_AssistantFeedback_AssistantQuery_AssistantQueryId",
                        column: x => x.AssistantQueryId,
                        principalSchema: "ai",
                        principalTable: "AssistantQuery",
                        principalColumn: "AssistantQueryId",
                        onDelete: ReferentialAction.Cascade);
                });

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
                filter: "[ValidationOutcome] <> 'Approved'")
                .Annotation("SqlServer:Include", new[] { "QuestionText", "ValidationOutcome", "ValidationDetail" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantQuery_SessionId",
                schema: "ai",
                table: "AssistantQuery",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantQuery_User",
                schema: "ai",
                table: "AssistantQuery",
                columns: new[] { "UserId", "AskedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantSession_User",
                schema: "ai",
                table: "AssistantSession",
                columns: new[] { "UserId", "StartedAtUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantFeedback",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "AssistantQuery",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "AssistantSession",
                schema: "ai");
        }
    }
}
