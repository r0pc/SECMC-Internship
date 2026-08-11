using DataIntelligence.Core.Enums;
using DataIntelligence.Infrastructure.Ai;

namespace DataIntelligence.UnitTests.Ai;

/// <summary>
/// The gate between the model's output and the database (FR-14, SOW 9 Risk 3).
/// </summary>
/// <remarks>
/// SOW 9 rates unsafe AI-generated SQL as High impact, which makes this the one component whose
/// tests are written adversarially: the interesting cases are not the queries that work but the
/// ones that must never reach SQL Server. Every rejection below is a statement that would have
/// executed if the validator were absent.
/// </remarks>
public class SqlSafetyValidatorTests
{
    private static readonly SqlSafetyValidator Validator = new();

    // ------------------------------------------------------------------ allowed

    [Theory]
    [InlineData("SELECT ReferenceDate, IndexValue FROM analytics.vw_Cpi")]
    [InlineData("SELECT TOP (10) * FROM analytics.vw_Sofr ORDER BY EffectiveDate DESC")]
    [InlineData("SELECT c.ReferenceYear, s.AverageRatePercent FROM analytics.vw_CpiAnnual c "
        + "JOIN analytics.vw_SofrAnnual s ON s.CalendarYear = c.ReferenceYear")]
    [InlineData("SELECT [analytics].[vw_Cpi].IndexValue FROM [analytics].[vw_Cpi]")]
    [InlineData("SELECT AVG(RatePercent) FROM analytics.vw_Sofr WHERE EffectiveDate >= '2025-01-01'")]
    public void ApprovesAReadOnlyQueryOverThePublishedViews(string sql)
    {
        var result = Validator.Validate(sql);

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
        Assert.True(result.IsApproved);
        Assert.NotNull(result.NormalizedSql);
    }

    // -------------------------------------------------------------- destructive

    [Theory]
    [InlineData("DROP TABLE core.CpiObservation")]
    [InlineData("DELETE FROM analytics.vw_Cpi")]
    [InlineData("UPDATE core.CpiObservation SET IndexValue = 0")]
    [InlineData("INSERT INTO core.CpiObservation (IndexValue) VALUES (1)")]
    [InlineData("TRUNCATE TABLE core.SofrDailyRate")]
    [InlineData("EXEC sp_executesql N'DROP TABLE core.CpiObservation'")]
    [InlineData("GRANT SELECT ON SCHEMA::core TO di_ai_readonly")]
    public void RejectsAnythingThatIsNotASelect(string sql)
    {
        Assert.NotEqual(AssistantValidationOutcome.Approved, Validator.Validate(sql).Outcome);
    }

    [Theory]
    // Stacked statements: the SELECT is a decoy for whatever follows the semicolon.
    [InlineData("SELECT 1 FROM analytics.vw_Cpi; DROP TABLE core.CpiObservation")]
    [InlineData("SELECT 1 FROM analytics.vw_Cpi\nGO\nDROP TABLE core.CpiObservation")]
    // A trailing semicolon is the same mechanism with the payload left off.
    [InlineData("SELECT 1 FROM analytics.vw_Cpi;;DELETE FROM core.CpiObservation")]
    public void RejectsMoreThanOneStatement(string sql)
    {
        Assert.Equal(AssistantValidationOutcome.RejectedSyntax, Validator.Validate(sql).Outcome);
    }

    [Theory]
    // A keyword split by a comment reassembles into DROP only after SQL Server strips the comment,
    // so the scan has to strip comments before it looks.
    [InlineData("SELECT 1 FROM analytics.vw_Cpi /* harmless */; DR/**/OP TABLE core.CpiObservation")]
    [InlineData("SELECT 1 FROM analytics.vw_Cpi -- \n; DROP TABLE core.CpiObservation")]
    [InlineData("/* leading */ DROP TABLE core.CpiObservation")]
    public void RejectsStatementsHiddenBehindComments(string sql)
    {
        Assert.NotEqual(AssistantValidationOutcome.Approved, Validator.Validate(sql).Outcome);
    }

    // ------------------------------------------------------- forbidden objects

    [Theory]
    // The audit log and the identity tables are the two things the assistant must never read:
    // the schema DENYs both to di_ai_readonly, and the validator refuses them first.
    [InlineData("SELECT * FROM sec.AppUser")]
    [InlineData("SELECT * FROM ai.AssistantQuery")]
    [InlineData("SELECT * FROM core.CpiObservation")]
    [InlineData("SELECT * FROM collect.RawPayload")]
    [InlineData("SELECT * FROM master.dbo.sysdatabases")]
    // Reached through a join rather than named first — the same object, one clause later.
    [InlineData("SELECT u.* FROM analytics.vw_Cpi c JOIN sec.AppUser u ON u.UserId = 1")]
    // CROSS APPLY introduces a table expression exactly as JOIN does.
    [InlineData("SELECT * FROM analytics.vw_Cpi c CROSS APPLY sec.AppUser u")]
    [InlineData("SELECT * FROM analytics.vw_Cpi c OUTER APPLY ai.AssistantQuery q")]
    // Nested inside a predicate rather than in the FROM clause.
    [InlineData("SELECT 1 FROM analytics.vw_Cpi WHERE EXISTS (SELECT 1 FROM sec.AppUser)")]
    [InlineData("SELECT 1 FROM analytics.vw_Cpi WHERE 1 IN (SELECT UserId FROM sec.AppUser)")]
    public void RejectsEveryObjectOutsideTheAnalyticsAllowList(string sql)
    {
        var result = Validator.Validate(sql);

        Assert.NotEqual(AssistantValidationOutcome.Approved, result.Outcome);
    }

    [Theory]
    // An unqualified name is not on the allow-list, and — the part worth pinning — it must be
    // *seen* to be rejected. A pattern matching only schema.object would not match this at all,
    // and an unmatched reference is an unchecked one.
    [InlineData("SELECT * FROM analytics.vw_Cpi c JOIN AppUser u ON u.UserId = 1")]
    [InlineData("SELECT * FROM vw_Cpi")]
    public void RejectsAnUnqualifiedObjectName(string sql)
    {
        Assert.NotEqual(AssistantValidationOutcome.Approved, Validator.Validate(sql).Outcome);
    }

    [Fact]
    public void RejectsBracketedAndSpacedNamesThatResolveToAForbiddenObject()
    {
        var result = Validator.Validate("SELECT * FROM [sec] . [AppUser]");

        Assert.Equal(AssistantValidationOutcome.RejectedForbiddenObject, result.Outcome);
    }

    [Fact]
    public void NamesTheOffendingObjectSoTheRejectionCanBeReviewed()
    {
        var result = Validator.Validate("SELECT * FROM sec.AppUser");

        Assert.Equal(AssistantValidationOutcome.RejectedForbiddenObject, result.Outcome);
        Assert.Contains("sec.AppUser", result.Detail);
    }

    // --------------------------------------------------- procedures and prefix

    [Theory]
    // 'sp_' and 'xp_' are prefixes, not words: a word-boundary test after the underscore never
    // fires, because '_' is itself a word character.
    [InlineData("SELECT * FROM analytics.vw_Cpi WHERE 1 = (SELECT 1 FROM sp_helpdb)")]
    [InlineData("SELECT * FROM analytics.vw_Cpi WHERE xp_cmdshell('dir') = 1")]
    public void RejectsSystemProcedurePrefixes(string sql)
    {
        Assert.Equal(AssistantValidationOutcome.RejectedSyntax, Validator.Validate(sql).Outcome);
    }

    // ------------------------------------------------------------- string data

    [Fact]
    public void DoesNotRejectAForbiddenWordAppearingInsideAStringLiteral()
    {
        // 'delete' here is data, not a statement — SQL Server never executes it. Rejecting this
        // would make ordinary questions fail for looking dangerous.
        var result = Validator.Validate(
            "SELECT SeriesTitle FROM analytics.vw_Cpi WHERE SeriesTitle = 'update and delete'");

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void StillRejectsASemicolonOutsideALiteralWhenALiteralIsPresent()
    {
        var result = Validator.Validate(
            "SELECT SeriesTitle FROM analytics.vw_Cpi WHERE SeriesTitle = 'a;b'; DROP TABLE core.CpiObservation");

        Assert.Equal(AssistantValidationOutcome.RejectedSyntax, result.Outcome);
    }

    [Fact]
    public void TreatsASemicolonInsideALiteralAsData()
    {
        var result = Validator.Validate(
            "SELECT SeriesTitle FROM analytics.vw_Cpi WHERE SeriesTitle = 'first;second'");

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
    }

    // -------------------------------------------------------------- row capping

    [Fact]
    public void InjectsARowCapWhenTheModelDidNotSetOne()
    {
        var result = Validator.Validate("SELECT ReferenceDate FROM analytics.vw_Cpi");

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
        Assert.Contains($"TOP ({SqlSafetyValidator.MaxRows})", result.NormalizedSql);
    }

    [Fact]
    public void KeepsTheModelsOwnRowLimitRatherThanStackingASecondOne()
    {
        var result = Validator.Validate("SELECT TOP (5) ReferenceDate FROM analytics.vw_Cpi");

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
        Assert.Contains("TOP (5)", result.NormalizedSql);
        Assert.DoesNotContain($"TOP ({SqlSafetyValidator.MaxRows})", result.NormalizedSql);
    }

    [Fact]
    public void PreservesDistinctWhenInjectingTheRowCap()
    {
        var result = Validator.Validate("SELECT DISTINCT ReferenceYear FROM analytics.vw_Cpi");

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
        Assert.Contains("DISTINCT", result.NormalizedSql);
        Assert.Contains($"TOP ({SqlSafetyValidator.MaxRows})", result.NormalizedSql);
    }

    [Fact]
    public void StripsCommentsFromTheStatementItApproves()
    {
        // The approved text is what executes, so a comment must not survive into it.
        var result = Validator.Validate(
            "SELECT ReferenceDate /* note */ FROM analytics.vw_Cpi -- trailing");

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
        Assert.DoesNotContain("/*", result.NormalizedSql);
        Assert.DoesNotContain("--", result.NormalizedSql);
    }

    // -------------------------------------------------------------- parameters

    [Fact]
    public void ApprovesAParameterisedQueryWhoseValuesAreAllSupplied()
    {
        var result = Validator.Validate(
            "SELECT IndexValue FROM analytics.vw_Cpi WHERE ReferenceDate = @month",
            new Dictionary<string, object?> { ["@month"] = "2025-06-01" });

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void AcceptsParameterNamesWithOrWithoutTheLeadingAt()
    {
        // The model is asked for "@name" and does not always comply; both spellings name the
        // same parameter, and rejecting one of them would be rejecting a correct query.
        var result = Validator.Validate(
            "SELECT IndexValue FROM analytics.vw_Cpi WHERE ReferenceDate = @month",
            new Dictionary<string, object?> { ["month"] = "2025-06-01" });

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void RejectsAPlaceholderWithNoValue()
    {
        // Left to the database this is "must declare the scalar variable @month" — recorded as an
        // execution failure, which reads as a platform fault rather than an incoherent pair.
        var result = Validator.Validate(
            "SELECT IndexValue FROM analytics.vw_Cpi WHERE ReferenceDate = @month",
            new Dictionary<string, object?>());

        Assert.Equal(AssistantValidationOutcome.RejectedSyntax, result.Outcome);
        Assert.Contains("@month", result.Detail);
    }

    [Fact]
    public void DropsAValueTheStatementNeverUsesRatherThanRefusingTheStatement()
    {
        // This was a rejection, on the reasoning that the model had described one query and written
        // another. It is not a safety property: an unused value is never placed in the statement,
        // never parsed, and cannot affect what is read. Meanwhile a small model leaves them behind
        // routinely — settling on a single date after considering a range leaves @from and @to
        // supplied and unused — and the statement it wrote is correct. Refusing it spent the whole
        // wait to reject a right answer.
        var result = Validator.Validate(
            "SELECT IndexValue FROM analytics.vw_Cpi WHERE ReferenceDate = @month",
            new Dictionary<string, object?>
            {
                ["@month"] = "2025-03-01",
                ["@from"] = "2025-01-01",
                ["@to"] = "2026-01-01"
            });

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);

        // Dropped, not merely tolerated. What is shown beside an answer has to be what ran, or the
        // reader is left working out how @from constrained a result it never touched.
        Assert.NotNull(result.BoundParameters);
        Assert.Equal(["@month"], result.BoundParameters.Keys);
        Assert.Equal("2025-03-01", result.BoundParameters["@month"]);
    }

    [Fact]
    public void KeepsEveryValueTheStatementDoesUse()
    {
        var result = Validator.Validate(
            "SELECT AVG(RatePercent) FROM analytics.vw_Sofr "
            + "WHERE EffectiveDate >= @from AND EffectiveDate < @to",
            new Dictionary<string, object?> { ["@from"] = "2025-01-01", ["@to"] = "2026-01-01" });

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
        Assert.NotNull(result.BoundParameters);
        Assert.Equal(2, result.BoundParameters.Count);
    }

    [Fact]
    public void ReportsNoBoundParametersForAStatementThatNeedsNone()
    {
        var result = Validator.Validate("SELECT IndexValue FROM analytics.vw_Cpi");

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
        Assert.NotNull(result.BoundParameters);
        Assert.Empty(result.BoundParameters);
    }

    [Fact]
    public void StillRejectsAPlaceholderWithNoValueEvenAlongsideAnUnusedOne()
    {
        // The two directions are not symmetrical. A value with no placeholder is harmless; a
        // placeholder with no value fails at the database, and relaxing one must not relax the
        // other.
        var result = Validator.Validate(
            "SELECT IndexValue FROM analytics.vw_Cpi WHERE ReferenceDate = @month",
            new Dictionary<string, object?> { ["@year"] = 2025 });

        Assert.Equal(AssistantValidationOutcome.RejectedSyntax, result.Outcome);
        Assert.Contains("@month", result.Detail);
    }

    [Fact]
    public void ApprovesADateRangeWhoseParametersAreNamedAfterSqlKeywords()
    {
        // '@' is not a word character, so there is a word boundary between it and what follows —
        // which means a naive \bFROM\b matches the "from" inside @from, reads the next token as a
        // table name, and rejects the query as referencing an object called AND. A date range is
        // the most common thing anyone asks for, and @from/@to the obvious names for one.
        var result = Validator.Validate(
            "SELECT AVG(RatePercent) FROM analytics.vw_Sofr "
            + "WHERE EffectiveDate >= @from AND EffectiveDate < @to",
            new Dictionary<string, object?> { ["@from"] = "2026-07-01", ["@to"] = "2026-08-01" });

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
    }

    [Theory]
    // The same trap for the forbidden-keyword scan: these are parameter names, not statements.
    [InlineData("@into")]
    [InlineData("@update")]
    [InlineData("@delete")]
    [InlineData("@create")]
    public void ApprovesAParameterNamedAfterAForbiddenKeyword(string parameter)
    {
        var result = Validator.Validate(
            $"SELECT IndexValue FROM analytics.vw_Cpi WHERE ReferenceDate = {parameter}",
            new Dictionary<string, object?> { [parameter] = "2025-06-01" });

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void StillRejectsARealForbiddenKeywordAlongsideSuchAParameter()
    {
        // The guard must not become a way to smuggle one in.
        var result = Validator.Validate(
            "SELECT IndexValue INTO x FROM analytics.vw_Cpi WHERE ReferenceDate = @into",
            new Dictionary<string, object?> { ["@into"] = "2025-06-01" });

        Assert.Equal(AssistantValidationOutcome.RejectedSyntax, result.Outcome);
    }

    [Fact]
    public void StillFindsTheObjectAfterAGenuineFromAlongsideSuchAParameter()
    {
        var result = Validator.Validate(
            "SELECT 1 FROM sec.AppUser WHERE Id = @from",
            new Dictionary<string, object?> { ["@from"] = 1 });

        Assert.Equal(AssistantValidationOutcome.RejectedForbiddenObject, result.Outcome);
    }

    [Fact]
    public void DoesNotMistakeAParameterInsideALiteralForAPlaceholder()
    {
        var result = Validator.Validate(
            "SELECT SeriesTitle FROM analytics.vw_Cpi WHERE SeriesTitle = 'costs @month'");

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
    }

    [Theory]
    // Configuration functions, not data — and not parameters either, which is why the placeholder
    // pattern has to exclude them rather than read '@@VERSION' as a parameter named VERSION.
    [InlineData("SELECT @@VERSION FROM analytics.vw_Cpi")]
    [InlineData("SELECT IndexValue, @@SERVERNAME FROM analytics.vw_Cpi")]
    public void RejectsServerConfigurationFunctions(string sql)
    {
        Assert.Equal(AssistantValidationOutcome.RejectedSyntax, Validator.Validate(sql).Outcome);
    }

    // ------------------------------------------------------------- degenerate

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TreatsAnAbsentStatementAsNoSqlRatherThanACrash(string? sql)
    {
        Assert.Equal(AssistantValidationOutcome.RejectedNoSql, Validator.Validate(sql!).Outcome);
    }

    [Fact]
    public void RejectsASelectThatReadsNoObjectAtAll()
    {
        // Nothing to authorise means nothing to approve; a constant SELECT is not a data question.
        Assert.Equal(AssistantValidationOutcome.RejectedSyntax, Validator.Validate("SELECT 1").Outcome);
    }

    [Fact]
    public void RejectsAQueryOverTheJoinBudget()
    {
        var sql = "SELECT 1 FROM analytics.vw_Cpi "
            + string.Concat(Enumerable.Repeat("JOIN analytics.vw_Sofr ON 1 = 1 ", 8));

        Assert.Equal(AssistantValidationOutcome.RejectedComplexity, Validator.Validate(sql).Outcome);
    }

    // ------------------------------------------------- analytical query shapes

    [Fact]
    public void ApprovesAWindowFunctionOverNestedDerivedTables()
    {
        // The shape the prompt teaches for "which month changed the most". It matters that this
        // passes: derived tables are the only way to express a CTE here, so if the validator ever
        // stopped accepting them the whole class of change-over-time questions would start being
        // refused with no obvious cause.
        const string sql = """
            SELECT TOP (1) DATEADD(month, -1, m.MonthStart) AS FromMonth, m.MonthStart AS ToMonth,
                   m.AvgRate - m.PrevAvgRate AS ChangeInPercentagePoints
            FROM (SELECT MonthStart, AvgRate, LAG(AvgRate) OVER (ORDER BY MonthStart) AS PrevAvgRate
                  FROM (SELECT DATEFROMPARTS(YEAR(EffectiveDate), MONTH(EffectiveDate), 1) AS MonthStart,
                               AVG(RatePercent) AS AvgRate
                        FROM analytics.vw_Sofr
                        WHERE EffectiveDate >= @from AND EffectiveDate < @to
                        GROUP BY DATEFROMPARTS(YEAR(EffectiveDate), MONTH(EffectiveDate), 1)) AS monthly) AS m
            WHERE m.PrevAvgRate IS NOT NULL
            ORDER BY ABS(m.AvgRate - m.PrevAvgRate) DESC
            """;

        var parameters = new Dictionary<string, object?> { ["@from"] = "2025-01-01", ["@to"] = "2026-01-01" };

        Assert.Equal(AssistantValidationOutcome.Approved, Validator.Validate(sql, parameters).Outcome);
    }

    [Fact]
    public void DoesNotOverrideATopTheQueryChoseForItself()
    {
        // A "which single month" question answers itself with TOP (1). Injecting the 2,000-row cap
        // over it would turn one row into two thousand and change the answer.
        const string sql = "SELECT TOP (1) EffectiveDate FROM analytics.vw_Sofr ORDER BY RatePercent DESC";

        var result = Validator.Validate(sql);

        Assert.Equal(AssistantValidationOutcome.Approved, result.Outcome);
        Assert.Contains("TOP (1)", result.NormalizedSql);
        Assert.DoesNotContain("TOP (2000)", result.NormalizedSql);
    }

    [Fact]
    public void RejectsACommonTableExpression()
    {
        // Pinned as a refusal rather than left undefined, because it is the shape a model reaches
        // for first and the prompt spends a rule talking it out of. A CTE fails the "starts with
        // SELECT" test, and its name would not be an allowed object either.
        const string sql = """
            WITH monthly AS (SELECT AVG(RatePercent) AS AvgRate FROM analytics.vw_Sofr)
            SELECT AvgRate FROM monthly
            """;

        Assert.NotEqual(AssistantValidationOutcome.Approved, Validator.Validate(sql).Outcome);
    }
}
