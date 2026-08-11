using DataIntelligence.Infrastructure.Ai;

namespace DataIntelligence.UnitTests.Ai;

/// <summary>
/// The pre-filter that answers a greeting without buying the answer from a model.
/// </summary>
/// <remarks>
/// Almost everything here is about one direction of one mistake. Sending "hi" to the model costs
/// about 3,600 prompt tokens, which is what happened before this existed and is therefore the worst
/// this filter can do by being too cautious. Deciding that a real question is chatter answers it
/// with a greeting — the platform telling someone it cannot address what it can — and nobody
/// reports a question they have decided not to ask, so that one never comes back. The bulk of these
/// tests are questions that must reach the model, not greetings that must not.
/// </remarks>
public class PleasantryFilterTests
{
    [Theory]
    [InlineData("hi")]
    [InlineData("hello")]
    [InlineData("hey")]
    [InlineData("yo")]
    [InlineData("good morning")]
    [InlineData("good evening")]
    [InlineData("how are you")]
    [InlineData("hi how are you")]
    [InlineData("how are you doing")]
    [InlineData("thanks")]
    [InlineData("thank you")]
    [InlineData("ok thanks")]
    [InlineData("bye")]
    public void AnswersAnOrdinaryGreetingWithoutTheModel(string question)
    {
        // The whole point of the filter, stated as the inputs it is allowed to keep. Each of these
        // used to build the full schema prompt to be told it was not a data question.
        Assert.True(PleasantryFilter.IsPleasantry(question));
    }

    [Theory]
    [InlineData("Hi!")]
    [InlineData("  HELLO  ")]
    [InlineData("hey there.")]
    [InlineData("Thanks!!")]
    [InlineData("ok, cool")]
    [InlineData("hi   how    are  you")]
    public void ReadsAGreetingThroughItsPunctuationAndCasing(string question)
    {
        // Nobody types a bare lowercase word. If the normalisation were the weak part, the filter
        // would fire on the one spelling nobody uses and cost a model call on every real greeting.
        Assert.True(PleasantryFilter.IsPleasantry(question));
    }

    [Theory]
    [InlineData("hi what is cpi")]
    [InlineData("how has inflation moved")]
    [InlineData("thanks, now show me sofr")]
    [InlineData("good year for cpi?")]
    [InlineData("hey what is the average sofr rate")]
    [InlineData("thanks — and the year before that")]
    [InlineData("how much")]
    [InlineData("hello world")]
    public void SendsAnythingCarryingAWordItDoesNotKnowToTheModel(string question)
    {
        // The regression that matters. A greeting sitting in front of a real question — "hi what is
        // cpi" — is the shape a heuristic gets wrong, because the opening words look like the whole
        // input. One unknown word anywhere is enough to give up, which is why "cpi", "inflation",
        // "sofr" and "much" each save their own sentence here.
        Assert.False(PleasantryFilter.IsPleasantry(question));
    }

    [Theory]
    [InlineData("What was CPI in June 2025?")]
    [InlineData("What is the average SOFR rate in 2025?")]
    [InlineData("How did CPI and SOFR move together in 2025?")]
    [InlineData("Which sources failed to collect this week?")]
    [InlineData("What was the year over year inflation rate for the last 3 months?")]
    [InlineData("Between which months did SOFR change the most in 2025?")]
    public void SendsTheQuestionsThePlatformAdvertisesToTheModel(string question)
    {
        // Taken verbatim from the text the assistant offers as examples of what to ask. A filter
        // that swallowed one of these would refuse the question the platform itself suggested,
        // which is the failure worth pinning to the exact wording.
        Assert.False(PleasantryFilter.IsPleasantry(question));
    }

    [Theory]
    [InlineData("2025")]
    [InlineData("hi 2025")]
    [InlineData("good morning, cpi for 2025")]
    [InlineData("ok 5")]
    public void SendsAnythingWithADigitInItToTheModel(string question)
    {
        // A digit is a period, a level or a threshold, and none of those belongs to a greeting. It
        // is checked before any word is, so a question that names a year cannot be talked into
        // chatter by the words around it.
        Assert.False(PleasantryFilter.IsPleasantry(question));
    }

    [Fact]
    public void SendsALongRunOfFamiliarWordsToTheModelAnyway()
    {
        // Five words it knows individually, and it still gives up. The vocabulary is the real gate;
        // this is the second one, so that a long sentence which happens to dodge every unknown word
        // is judged by the model rather than here.
        Assert.False(PleasantryFilter.IsPleasantry("hi hello good morning thanks"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    [InlineData("...")]
    public void TreatsAnInputWithNoWordsInItAsSomethingForTheModel(string question)
    {
        // Nothing to allow-list is not the same as everything allowed. An input of pure punctuation
        // must not fall through the "every word is known" test on the technicality that it has no
        // words — that is the shape a vacuous check goes wrong in.
        Assert.False(PleasantryFilter.IsPleasantry(question));
    }

    [Fact]
    public void TreatsANullQuestionAsSomethingForTheModel()
    {
        // Runs before any validation the API does, and a filter that threw here would turn a
        // malformed request into a 500 on the answer path.
        Assert.False(PleasantryFilter.IsPleasantry(null));
    }
}
