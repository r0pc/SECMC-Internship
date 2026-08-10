using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssistantSessionTranscriptJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TranscriptJson",
                schema: "ai",
                table: "AssistantSession",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssistantSession_TranscriptJson",
                schema: "ai",
                table: "AssistantSession",
                sql: "[TranscriptJson] IS NULL OR ISJSON([TranscriptJson]) = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AssistantSession_TranscriptJson",
                schema: "ai",
                table: "AssistantSession");

            migrationBuilder.DropColumn(
                name: "TranscriptJson",
                schema: "ai",
                table: "AssistantSession");
        }
    }
}
