using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssistantExplanationAndRefusalKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantQuery_Rejected",
                schema: "ai",
                table: "AssistantQuery");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssistantQuery_Validation",
                schema: "ai",
                table: "AssistantQuery");

            migrationBuilder.AlterColumn<string>(
                name: "ValidationOutcome",
                schema: "ai",
                table: "AssistantQuery",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: false,
                defaultValue: "Pending",
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldUnicode: false,
                oldMaxLength: 20,
                oldDefaultValue: "Pending");

            migrationBuilder.AddColumn<string>(
                name: "Explanation",
                schema: "ai",
                table: "AssistantQuery",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantQuery_Rejected",
                schema: "ai",
                table: "AssistantQuery",
                column: "AskedAtUtc",
                descending: new bool[0],
                filter: "[ValidationOutcome] <> 'Approved' AND [ValidationOutcome] <> 'Pending' AND [ValidationOutcome] <> 'NotADataQuestion'")
                .Annotation("SqlServer:Include", new[] { "QuestionText", "ValidationOutcome", "ValidationDetail" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssistantQuery_Validation",
                schema: "ai",
                table: "AssistantQuery",
                sql: "[ValidationOutcome] IN ('Pending','Approved','RejectedNotSelect','RejectedForbiddenObject','RejectedSyntax','RejectedComplexity','RejectedNoSql','NotADataQuestion')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantQuery_Rejected",
                schema: "ai",
                table: "AssistantQuery");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssistantQuery_Validation",
                schema: "ai",
                table: "AssistantQuery");

            migrationBuilder.DropColumn(
                name: "Explanation",
                schema: "ai",
                table: "AssistantQuery");

            migrationBuilder.AlterColumn<string>(
                name: "ValidationOutcome",
                schema: "ai",
                table: "AssistantQuery",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending",
                oldClrType: typeof(string),
                oldType: "varchar(30)",
                oldUnicode: false,
                oldMaxLength: 30,
                oldDefaultValue: "Pending");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantQuery_Rejected",
                schema: "ai",
                table: "AssistantQuery",
                column: "AskedAtUtc",
                descending: new bool[0],
                filter: "[ValidationOutcome] <> 'Approved'")
                .Annotation("SqlServer:Include", new[] { "QuestionText", "ValidationOutcome", "ValidationDetail" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssistantQuery_Validation",
                schema: "ai",
                table: "AssistantQuery",
                sql: "[ValidationOutcome] IN ('Pending','Approved','RejectedNotSelect','RejectedForbiddenObject','RejectedSyntax','RejectedComplexity','RejectedNoSql')");
        }
    }
}
