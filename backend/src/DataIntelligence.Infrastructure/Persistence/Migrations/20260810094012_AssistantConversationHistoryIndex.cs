using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssistantConversationHistoryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantQuery_SessionId",
                schema: "ai",
                table: "AssistantQuery");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantQuery_Session",
                schema: "ai",
                table: "AssistantQuery",
                columns: new[] { "SessionId", "AskedAtUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantQuery_Session",
                schema: "ai",
                table: "AssistantQuery");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantQuery_SessionId",
                schema: "ai",
                table: "AssistantQuery",
                column: "SessionId");
        }
    }
}
