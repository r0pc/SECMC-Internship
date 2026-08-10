using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssistantUnreadableResponseOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AssistantQuery_Validation",
                schema: "ai",
                table: "AssistantQuery");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssistantQuery_Validation",
                schema: "ai",
                table: "AssistantQuery",
                sql: "[ValidationOutcome] IN ('Pending','Approved','RejectedNotSelect','RejectedForbiddenObject','RejectedSyntax','RejectedComplexity','RejectedNoSql','NotADataQuestion','RejectedUnreadableResponse')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AssistantQuery_Validation",
                schema: "ai",
                table: "AssistantQuery");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssistantQuery_Validation",
                schema: "ai",
                table: "AssistantQuery",
                sql: "[ValidationOutcome] IN ('Pending','Approved','RejectedNotSelect','RejectedForbiddenObject','RejectedSyntax','RejectedComplexity','RejectedNoSql','NotADataQuestion')");
        }
    }
}
