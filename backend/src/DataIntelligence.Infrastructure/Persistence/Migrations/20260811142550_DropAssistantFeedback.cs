using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Drops <c>ai.AssistantFeedback</c>: thumbs up/down on an answer is no longer a feature, so
    /// the endpoint that wrote the table and the table itself both go.
    /// </summary>
    /// <remarks>
    /// Written as its own migration rather than left for the model snapshot to carry, because an
    /// unmigrated drop is not a drop that never happens — it is one that arrives folded into
    /// whatever migration is added next, under a name that says nothing about deleting a table of
    /// user-submitted data.
    /// <para>
    /// The rows go with the table. There is nowhere to move them to: nothing else records which
    /// turn a person judged helpful, and the turn ids they carry only mean anything against a
    /// feature that no longer exists.
    /// </para>
    /// </remarks>
    /// <inheritdoc />
    public partial class DropAssistantFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantFeedback",
                schema: "ai");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rebuilds the table, empty. Reverting restores the shape, never the feedback — Up
            // deleted the only copy of it. Note also that the foreign key to ai.AssistantQuery this
            // table was created with is not restored: that table was dropped two migrations ago.
            migrationBuilder.CreateTable(
                name: "AssistantFeedback",
                schema: "ai",
                columns: table => new
                {
                    AssistantQueryId = table.Column<long>(type: "bigint", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsHelpful = table.Column<bool>(type: "bit", nullable: false),
                    SubmittedAtPkt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantFeedback", x => x.AssistantQueryId);
                });
        }
    }
}
