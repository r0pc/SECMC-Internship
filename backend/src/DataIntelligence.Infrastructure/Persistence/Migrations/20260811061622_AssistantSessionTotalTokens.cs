using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataIntelligence.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>ai.AssistantSession.TotalTokens</c>: what a whole conversation has cost the model,
    /// so the chat list can show it without parsing every transcript to draw itself.
    /// </summary>
    /// <inheritdoc />
    public partial class AssistantSessionTotalTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TotalTokens",
                schema: "ai",
                table: "AssistantSession",
                type: "int",
                nullable: true);

            // Backfilled, because the alternative is a column that is only true about conversations
            // held after today. The figures are already in the transcripts — every turn has carried
            // totalTokens since the document became the store — so the existing chats can be given
            // an accurate number rather than a NULL that reads as "this cost nothing".
            //
            // SUM over OPENJSON matches AssistantService.SumTokens exactly, and does so by
            // accident of neither: SUM ignores NULL inputs and returns NULL when every input is
            // NULL, which is the same rule as "count the turns that reported usage, and say
            // nothing when none did".
            //
            // Sessions with no transcript are left alone rather than set to 0. One exists from the
            // moment a question arrives, so an abandoned first question leaves a session with
            // nothing in it, and NULL is what is true about it.
            migrationBuilder.Sql("""
                UPDATE  s
                SET     s.TotalTokens = x.TotalTokens
                FROM    ai.AssistantSession AS s
                CROSS APPLY (
                    SELECT  SUM(t.TotalTokens) AS TotalTokens
                    FROM    OPENJSON(s.TranscriptJson, '$.turns')
                    WITH (TotalTokens INT '$.totalTokens') AS t
                ) AS x
                WHERE   s.TranscriptJson IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo for the backfill: the numbers it wrote are derived from the
            // transcripts, which this migration never touched, and dropping the column discards
            // them along with everything else the column held.
            migrationBuilder.DropColumn(
                name: "TotalTokens",
                schema: "ai",
                table: "AssistantSession");
        }
    }
}
