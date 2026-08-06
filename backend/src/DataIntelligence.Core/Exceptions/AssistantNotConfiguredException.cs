namespace DataIntelligence.Core.Exceptions;

/// <summary>
/// The assistant was asked a question while its LLM provider is not configured — almost always a
/// missing API key (SOW 3: secrets come from user secrets or the environment, never the repo).
/// </summary>
/// <remarks>
/// Its own type rather than a bare <see cref="InvalidOperationException"/> so the endpoint can
/// answer 503 with a message an operator can act on, instead of a 500 that says nothing. The
/// dashboards do not need the assistant, so a missing key must degrade this one endpoint rather
/// than stop the API from starting.
/// </remarks>
public sealed class AssistantNotConfiguredException : Exception
{
    public AssistantNotConfiguredException(string message) : base(message)
    {
    }
}
