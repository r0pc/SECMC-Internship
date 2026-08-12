using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The <c>sec</c> schema — accounts, roles and grants (FR-9) — and the foreign key from the
    /// assistant's audit log that has been waiting for it.
    /// </summary>
    /// <remarks>
    /// <c>AiAssistantAuditLog</c> created <c>ai.AssistantSession.UserId</c> without its constraint,
    /// because <c>sec.AppUser</c> did not exist yet. It does now, so the constraint
    /// <c>docs/database-schema.sql</c> always specified is added here and that TODO is closed.
    /// <para>
    /// The three roles are seeded; no account is. A password cannot go in a migration — it would be
    /// in source control and identical in every deployment — so the first administrator is created
    /// at startup from configuration instead. See <c>AdminAccountSeeder</c>.
    /// </para>
    /// </remarks>
    public partial class AuthenticationTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sec");

            migrationBuilder.CreateTable(
                name: "AppUser",
                schema: "sec",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SecurityStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAtPkt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())"),
                    LastLoginAtPkt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUser", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Role",
                schema: "sec",
                columns: table => new
                {
                    RoleId = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Role", x => x.RoleId);
                });

            migrationBuilder.CreateTable(
                name: "UserRole",
                schema: "sec",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RoleId = table.Column<byte>(type: "tinyint", nullable: false),
                    GrantedAtPkt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "DATEADD(hour, 5, SYSUTCDATETIME())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRole", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRole_Role",
                        column: x => x.RoleId,
                        principalSchema: "sec",
                        principalTable: "Role",
                        principalColumn: "RoleId");
                    table.ForeignKey(
                        name: "FK_UserRole_User",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "AppUser",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "sec",
                table: "Role",
                columns: new[] { "RoleId", "Description", "Name" },
                values: new object[,]
                {
                    { (byte)1, "Full access: configuration, user management, all data.", "Administrator" },
                    { (byte)2, "Dashboards, drill-down and the AI query assistant.", "Analyst" },
                    { (byte)3, "Read-only dashboards.", "Viewer" }
                });

            migrationBuilder.CreateIndex(
                name: "UQ_AppUser_Email",
                schema: "sec",
                table: "AppUser",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Role_Name",
                schema: "sec",
                table: "Role",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRole_RoleId",
                schema: "sec",
                table: "UserRole",
                column: "RoleId");

            // Every question asked before this migration was recorded against a hard-coded user id
            // that now has nothing to point at, and AddForeignKey below would fail on those rows —
            // taking the whole deployment with it, on the one kind of database that has been in
            // use the longest.
            //
            // So each distinct orphaned id gets an account it can reference. The rows are the audit
            // record of what was asked (NFR Auditability); discarding them to make a constraint
            // apply would be destroying evidence to tidy up the schema.
            //
            // These accounts cannot be signed in to. PasswordHash is not a hash: ASP.NET Identity
            // v3 stores base64 of a byte array, this string cannot decode as one, and verification
            // therefore fails for every possible password rather than for all but one. IsActive = 0
            // means the token pipeline would reject them even if it somehow succeeded. The same
            // reasoning, and the same marker, as the placeholder row in docs/database-schema.sql.
            migrationBuilder.Sql("""
                SET IDENTITY_INSERT sec.AppUser ON;

                INSERT INTO sec.AppUser (UserId, Email, DisplayName, PasswordHash, IsActive)
                SELECT  DISTINCT s.UserId,
                        CONCAT(N'retired-user-', s.UserId, N'@local'),
                        CONCAT(N'Retired account ', s.UserId, N' (pre-FR-9)'),
                        N'!NO-LOGIN!',
                        0
                FROM    ai.AssistantSession AS s
                WHERE   NOT EXISTS (SELECT 1 FROM sec.AppUser AS u WHERE u.UserId = s.UserId);

                SET IDENTITY_INSERT sec.AppUser OFF;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_AssistantSession_User",
                schema: "ai",
                table: "AssistantSession",
                column: "UserId",
                principalSchema: "sec",
                principalTable: "AppUser",
                principalColumn: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssistantSession_User",
                schema: "ai",
                table: "AssistantSession");

            migrationBuilder.DropTable(
                name: "UserRole",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "Role",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "AppUser",
                schema: "sec");
        }
    }
}
