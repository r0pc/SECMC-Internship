using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssistantTurnsInTranscriptJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssistantFeedback_AssistantQuery_AssistantQueryId",
                schema: "ai",
                table: "AssistantFeedback");

            migrationBuilder.CreateSequence(
                name: "AssistantTurnId",
                schema: "ai");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropSequence(
                name: "AssistantTurnId",
                schema: "ai");

            migrationBuilder.AddForeignKey(
                name: "FK_AssistantFeedback_AssistantQuery_AssistantQueryId",
                schema: "ai",
                table: "AssistantFeedback",
                column: "AssistantQueryId",
                principalSchema: "ai",
                principalTable: "AssistantQuery",
                principalColumn: "AssistantQueryId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
