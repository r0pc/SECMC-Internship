namespace DataIntelligence.Core.Exceptions;

/// <summary>
/// The assistant was asked a question while the model it was asked to use is not usable — a
/// missing API key for the hosted gateway (SOW 3: secrets come from user secrets or the
/// environment, never the repo), or a local model server that is not running or has not downloaded
/// the model it was asked for.
/// </summary>
/// <remarks>
/// Its own type rather than a bare <see cref="InvalidOperationException"/> so the endpoint can
/// answer 503 with a message an operator can act on, instead of a 500 that says nothing. The
/// dashboards do not need the assistant, so a missing key must degrade this one endpoint rather
/// than stop the API from starting.
/// <para>
/// Every case it covers is a setup problem with a named fix — a setting to fill in, a server to
/// start, a model to pull — which is what makes one type reasonable for all of them. A fault the
/// reader cannot act on does not belong here.
/// </para>
/// </remarks>
public sealed class AssistantNotConfiguredException : Exception
{
    public AssistantNotConfiguredException(string message) : base(message)
    {
    }

    /// <summary>
    /// Keeps the transport failure underneath the advice. The message says to start the local model
    /// server; the inner exception says whether the connection was refused, timed out, or resolved
    /// to the wrong host — which is the part that matters when starting it does not help.
    /// </summary>
    public AssistantNotConfiguredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
