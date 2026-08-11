// backend/src/DataIntelligence.Infrastructure/Ai/PleasantryFilter.cs
using System.Text.RegularExpressions;

namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// Recognises the few inputs that cannot be questions about data — "hi", "thanks", "good morning" —
/// so the assistant can answer them from the text it already holds instead of buying the answer
/// from a model.
/// </summary>
/// <remarks>
/// "hi" costs about 3,600 prompt tokens today. The whole schema context is assembled and sent, the
/// model replies <c>{"sql":null,"refusal":"not_a_data_question"}</c>, and the assistant answers with
/// <c>AssistantService.Greeting</c> — fixed text that was never in doubt. Nothing that came back was
/// used, and the same is true of every greeting anyone will ever type.
/// <para>
/// The two ways of being wrong here are nothing like each other, and every decision below follows
/// from that. Sending a greeting to the model costs the tokens it costs today. Deciding that a real
/// question is chatter answers it with a greeting — telling someone the platform cannot address
/// something it can, in a reply that looks deliberate — and a user who believes that stops asking.
/// There is no later correction for it, because nobody reports a question they have decided not to
/// ask. So this is an allow-list of whole words rather than a rule about their shape: one word this
/// class has never heard of, anywhere in the input, and the question goes to the model exactly as it
/// did before.
/// </para>
/// <para>
/// What makes "every word is known" safe rather than lucky is the vocabulary itself, which holds no
/// noun, verb, period or comparison that a question about CPI, SOFR or the collection log could be
/// assembled from. That is also the test for adding to it: a word belongs here because it cannot
/// carry a question, not because it turns up in greetings. "much" would fail that test — "how much?"
/// is a real follow-up — and so would "today", which is a period a follow-up can be built entirely
/// out of.
/// </para>
/// </remarks>
public static class PleasantryFilter
{
    /// <summary>
    /// The longest input this will judge at all. Anything longer goes to the model.
    /// </summary>
    /// <remarks>
    /// A second gate behind the vocabulary rather than a measure of politeness: "hi how are you" is
    /// four words and everything anyone means by a greeting fits in that. Its job is to make a long
    /// sentence that happens to avoid every unknown word — however that came about — go to the model
    /// on length alone. A greeting turned away here costs the tokens it costs today, which is the
    /// error this class is allowed to make.
    /// </remarks>
    public const int MaxWords = 4;

    /// <summary>
    /// Every word this class recognises. Nothing here can name a series, a period, a figure or a
    /// comparison, which is what lets the whole input be judged from its parts.
    /// </summary>
    /// <remarks>
    /// Spelling variants are listed rather than reduced by a stemmer, because a stemmer is a rule
    /// about shape and this must not have one: "thx" and "tysm" are worth their two entries, and a
    /// spelling nobody thought of simply goes to the model. Left out on purpose are the words that
    /// read as pleasantries but can also carry a question — "much", "today", "later", "well" — and
    /// every filler that would let one in behind it.
    /// </remarks>
    private static readonly HashSet<string> Vocabulary = new(StringComparer.Ordinal)
    {
        // Greetings, and the spellings people actually type.
        "hi", "hii", "hiya", "hey", "heya", "hello", "hullo", "howdy", "hola", "yo", "sup",
        "greetings",

        // "good morning", "good evening", "good day". None of them is a period a query could ask
        // for: the data is dated, and a date carries digits, which are refused outright.
        "good", "morning", "afternoon", "evening", "night", "day",

        // "how are you", "how are you doing" — and nothing else these four can be arranged into.
        "how", "are", "you", "doing",

        // Thanks.
        "thanks", "thank", "thankyou", "ty", "thx", "tysm", "cheers", "appreciate", "appreciated",

        // Acknowledgements, which arrive on their own after an answer.
        "ok", "okay", "cool", "nice", "great", "awesome", "perfect",

        "please", "welcome",

        "bye", "byebye", "goodbye",

        // "hey there", "thanks mate".
        "there", "mate"
    };

    /// <summary>
    /// Whether this input can be answered as a greeting without asking a model anything.
    /// </summary>
    /// <remarks>
    /// False is the answer whenever there is any doubt at all, including for an input that is empty
    /// or holds no letters: false means the question takes the path it has always taken, so nothing
    /// is lost by it beyond the tokens already being spent.
    /// </remarks>
    public static bool IsPleasantry(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return false;
        }

        // A digit anywhere ends it before a word is examined. Every period, level and threshold a
        // question can name is written with one — "cpi 2025", "above 5", "june 2025 vs 2024" — so
        // this rules out a whole class of questions at a stroke, and rules out nothing a greeting
        // ever contained.
        if (question.Any(char.IsDigit))
        {
            return false;
        }

        // Splitting on everything that is not a letter is what normalises "hi!", "hello," and
        // "  hey   there  " without a separate trimming pass. It is not a way of ignoring
        // punctuation: a word cut in half by some — "how're" becoming "how" and "re" — leaves a
        // fragment that is in no vocabulary, so the question goes to the model, which is the side
        // this is meant to fail towards.
        var words = NotLetters
            .Split(question.ToLowerInvariant())
            .Where(word => word.Length > 0)
            .ToArray();

        return words.Length > 0
            && words.Length <= MaxWords
            && words.All(Vocabulary.Contains);
    }

    private static readonly Regex NotLetters = new(@"[^\p{L}]+", RegexOptions.Compiled);
}
