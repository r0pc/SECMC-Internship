// backend/src/DataIntelligence.Infrastructure/Ai/SqlSafetyValidator.cs
using System.Text.RegularExpressions;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;

namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// Enforces the SQL-safety rules the AI assistant must never cross: read-only, single statement,
/// analytics.* only (SOW 9).
/// </summary>
/// <remarks>
/// This is a defence-in-depth layer, not the only one — the executor also runs on the
/// <c>di_ai_readonly</c> login, which physically cannot write or see outside <c>analytics</c>.
/// A statement that somehow slipped past this validator would still be refused by the database.
/// Deliberately regex/token based rather than a full T-SQL parser: the surface this needs to
/// accept is narrow (SELECT over nine known views), and a narrow allowlist is easier to reason
/// about than a general-purpose parser trying to prove a query is safe.
/// </remarks>
public sealed class SqlSafetyValidator : ISqlSafetyValidator
{
    /// <summary>The only objects a generated query may reference — see docs/database-schema.sql §5.</summary>
    private static readonly HashSet<string> AllowedObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "analytics.vw_Cpi",
        "analytics.vw_CpiMonthlyChange",
        "analytics.vw_CpiAnnual",
        "analytics.vw_Sofr",
        "analytics.vw_SofrAnnual",
        "analytics.vw_LatestIndicator",
        "analytics.vw_CpiRevision",
        "analytics.vw_SofrRevision",
        "analytics.vw_CollectionHealth"
    };

    private static readonly string[] ForbiddenKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "ALTER", "CREATE", "TRUNCATE",
        "GRANT", "DENY", "REVOKE", "EXEC", "EXECUTE", "SP_EXECUTESQL", "OPENROWSET",
        "OPENQUERY", "OPENDATASOURCE", "BULK", "INTO", "BACKUP", "RESTORE", "SHUTDOWN",
        "DBCC", "WAITFOR", "XP_", "SP_"
    ];

    /// <summary>
    /// Every object a statement reads. <c>APPLY</c> is included because <c>CROSS APPLY</c> and
    /// <c>OUTER APPLY</c> introduce a table expression exactly as <c>JOIN</c> does — matching only
    /// FROM and JOIN would let <c>CROSS APPLY sec.AppUser</c> through unexamined.
    /// </summary>
    /// <remarks>
    /// The name is captured whole, including any schema and database parts, and is not required to
    /// be two-part. An unqualified <c>FROM vw_Cpi</c> must be *seen* and then rejected for not
    /// being on the allow-list; a pattern that only matched <c>schema.object</c> would not match it
    /// at all, and an unmatched reference is an unchecked one.
    /// </remarks>
    private static readonly Regex ObjectReference = new(
        @"\b(?:FROM|JOIN|APPLY)\s+(?!\()(\[?[\w$@#]+\]?(?:\s*\.\s*\[?[\w$@#]*\]?)*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches the <c>sp_</c>/<c>xp_</c> procedure prefixes as prefixes, not whole words.</summary>
    /// <remarks>
    /// A <c>\bSP_\b</c> word-boundary test never fires on <c>sp_executesql</c>: <c>_</c> is a word
    /// character, so there is no boundary between <c>sp_</c> and what follows it. The prefix has to
    /// be matched together with the name it prefixes.
    /// </remarks>
    private static readonly Regex ProcedurePrefix = new(
        @"\b(?:sp|xp)_\w+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Single-quoted literals, including the doubled-quote escape.</summary>
    private static readonly Regex StringLiteral = new(
        @"'(?:[^']|'')*'", RegexOptions.Compiled);

    /// <summary>Row cap the generated SELECT must not exceed, enforced with an injected TOP.</summary>
    public const int MaxRows = 2000;

    /// <summary>Crude complexity ceiling: more joins than this is almost certainly a mistake, not a real question.</summary>
    private const int MaxJoins = 6;

    public SqlValidationResult Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new SqlValidationResult(
                AssistantValidationOutcome.RejectedNoSql, "The model returned no SQL.", null);
        }

        var trimmed = StripComments(sql).Trim().TrimEnd(';', ' ', '\r', '\n');

        // Everything below inspects a copy with string literals blanked out. A literal is inert
        // data to SQL Server, so a title containing "insert" or an apostrophe is a false rejection
        // waiting to happen — while the statement actually executed keeps its literals intact.
        var inspected = StringLiteral.Replace(trimmed, "''");

        // Exactly one statement: a semicolon anywhere in the middle, or a batch separator, means
        // more than one statement is being smuggled through.
        if (inspected.Contains(';', StringComparison.Ordinal)
            || Regex.IsMatch(inspected, @"(?<![\w])GO(?![\w])", RegexOptions.IgnoreCase))
        {
            return new SqlValidationResult(
                AssistantValidationOutcome.RejectedSyntax,
                "The statement contains more than one batch.", null);
        }

        if (!Regex.IsMatch(inspected, @"^\s*SELECT\b", RegexOptions.IgnoreCase))
        {
            return new SqlValidationResult(
                AssistantValidationOutcome.RejectedNotSelect,
                "Only a single SELECT statement is permitted.", null);
        }

        foreach (var keyword in ForbiddenKeywords)
        {
            if (Regex.IsMatch(inspected, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase))
            {
                return new SqlValidationResult(
                    AssistantValidationOutcome.RejectedSyntax,
                    $"'{keyword}' is not permitted in a generated query.", null);
            }
        }

        if (ProcedurePrefix.Match(inspected) is { Success: true } procedure)
        {
            return new SqlValidationResult(
                AssistantValidationOutcome.RejectedSyntax,
                $"'{procedure.Value}' is not permitted in a generated query.", null);
        }

        var references = ObjectReference.Matches(inspected)
            .Select(m => NormalizeReference(m.Groups[1].Value))
            .ToList();

        if (references.Count == 0)
        {
            return new SqlValidationResult(
                AssistantValidationOutcome.RejectedSyntax,
                "Could not identify the table(s) this query reads from.", null);
        }

        var forbidden = references.FirstOrDefault(r => !AllowedObjects.Contains(r));
        if (forbidden is not null)
        {
            return new SqlValidationResult(
                AssistantValidationOutcome.RejectedForbiddenObject,
                $"'{forbidden}' is not one of the published analytics views.", null);
        }

        if (references.Count - 1 > MaxJoins)
        {
            return new SqlValidationResult(
                AssistantValidationOutcome.RejectedComplexity,
                $"The query joins {references.Count} objects, over the {MaxJoins}-join limit.", null);
        }

        var normalized = InjectRowCap(trimmed);

        return new SqlValidationResult(AssistantValidationOutcome.Approved, null, normalized);
    }

    /// <summary>
    /// Adds <c>TOP (MaxRows)</c> if the model did not already limit the result itself, so a
    /// question with a huge answer cannot return an unbounded result set.
    /// </summary>
    private static string InjectRowCap(string sql)
    {
        if (Regex.IsMatch(sql, @"^\s*SELECT\s+(DISTINCT\s+)?TOP\s*\(", RegexOptions.IgnoreCase))
        {
            return sql;
        }

        return Regex.Replace(
            sql, @"^\s*SELECT\s+(DISTINCT\s+)?", $"SELECT $1TOP ({MaxRows}) ",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// Reduces a captured object reference to a comparable form: brackets and the whitespace
    /// SQL Server tolerates around the dots removed, so <c>[analytics] . [vw_Cpi]</c> and
    /// <c>analytics.vw_Cpi</c> are recognised as the same object rather than the second being
    /// allowed and the first slipping through unmatched.
    /// </summary>
    private static string NormalizeReference(string reference) =>
        reference.Replace("[", "").Replace("]", "").Replace(" ", "")
            .Replace("\t", "").Replace("\r", "").Replace("\n", "");

    /// <summary>
    /// Strips <c>--</c> and <c>/* */</c> comments before inspection, since a keyword hidden
    /// inside a comment would otherwise dodge the forbidden-keyword scan while still being sent
    /// to SQL Server verbatim.
    /// </summary>
    private static string StripComments(string sql)
    {
        var noBlockComments = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(noBlockComments, @"--[^\r\n]*", " ");
    }
}