using DataIntelligence.Core.Enums;
using DataIntelligence.Infrastructure.Ai;
using Microsoft.Extensions.Caching.Memory;

namespace DataIntelligence.UnitTests.Ai;

/// <summary>
/// What counts as the same question, and what does not.
/// </summary>
/// <remarks>
/// The cache is safe because the model is called at temperature 0 and is therefore a deterministic
/// function of its input — so reusing its output for a byte-identical input cannot produce a
/// statement the model would not have produced. Everything worth testing here is about that word
/// "identical": the key has to move whenever anything the model would have seen has moved, or the
/// identity stops holding and the cache starts answering a question nobody asked.
/// </remarks>
public class AssistantPlanCacheTests
{
    private const string Schema = "analytics.vw_Cpi(ReferenceDate, IndexValue)\nToday's date is 2026-08-11.";

    private static readonly CachedPlan Plan = new(
        "SELECT IndexValue FROM analytics.vw_Cpi WHERE ReferenceDate = @month",
        new Dictionary<string, object?> { ["@month"] = "2025-06-01" },
        "Reads the monthly CPI index value.",
        "deepseek-v4-flash");

    private static AssistantPlanCache New() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void ReturnsNothingForAQuestionItHasNotSeen()
    {
        Assert.Null(New().Find("what was cpi in june 2025", Schema, AssistantModelChoice.Cloud));
    }

    [Fact]
    public void ReturnsTheStatementWhenTheSameQuestionIsAskedAgain()
    {
        var cache = New();
        cache.Remember("what was cpi in june 2025", Schema, AssistantModelChoice.Cloud, Plan);

        var found = cache.Find("what was cpi in june 2025", Schema, AssistantModelChoice.Cloud);

        Assert.NotNull(found);
        Assert.Equal(Plan.Sql, found!.Sql);
        Assert.Equal("2025-06-01", found.Parameters["@month"]);
        Assert.Equal("deepseek-v4-flash", found.ModelName);
    }

    [Theory]
    // Only case and whitespace. Nothing cleverer: two questions differing in wording may want
    // different statements, and the cost of missing a hit is one model call — which is what would
    // have happened anyway.
    [InlineData("What Was CPI In June 2025")]
    [InlineData("  what was cpi   in june 2025  ")]
    [InlineData("what was cpi\tin june 2025")]
    public void TreatsCasingAndWhitespaceAsTheSameQuestion(string variant)
    {
        var cache = New();
        cache.Remember("what was cpi in june 2025", Schema, AssistantModelChoice.Cloud, Plan);

        Assert.NotNull(cache.Find(variant, Schema, AssistantModelChoice.Cloud));
    }

    [Fact]
    public void DoesNotServeOneModelSAnswerToAnother()
    {
        // The two are not interchangeable in quality. Someone who switches to the cloud model after
        // a poor local answer is asking for that model's work, and handing back the local model's
        // statement would silently deny them the thing they asked for.
        var cache = New();
        cache.Remember("what was cpi in june 2025", Schema, AssistantModelChoice.Cloud, Plan);

        Assert.Null(cache.Find("what was cpi in june 2025", Schema, AssistantModelChoice.Local));
    }

    [Fact]
    public void ForgetsTheStatementWhenTheDateMoves()
    {
        // "What was inflation last month?" resolves to a different window tomorrow. Today's date
        // lives inside the schema context, so keying on that context is what makes this hold
        // without a separate rule about which questions are relative.
        var cache = New();
        cache.Remember("what was inflation last month", Schema, AssistantModelChoice.Cloud, Plan);

        var tomorrow = Schema.Replace("2026-08-11", "2026-08-12");

        Assert.Null(cache.Find("what was inflation last month", tomorrow, AssistantModelChoice.Cloud));
    }

    [Fact]
    public void ForgetsTheStatementWhenTheSchemaChanges()
    {
        // A view gaining a column invalidates every remembered statement, which is correct: one
        // written against the old column list may no longer be the best available.
        var cache = New();
        cache.Remember("what was cpi in june 2025", Schema, AssistantModelChoice.Cloud, Plan);

        var widened = Schema.Replace("IndexValue)", "IndexValue, RevisionNumber)");

        Assert.Null(cache.Find("what was cpi in june 2025", widened, AssistantModelChoice.Cloud));
    }

    [Fact]
    public void KeepsDifferentQuestionsApart()
    {
        var cache = New();
        cache.Remember("what was cpi in june 2025", Schema, AssistantModelChoice.Cloud, Plan);

        Assert.Null(cache.Find("what was cpi in july 2025", Schema, AssistantModelChoice.Cloud));
    }
}
