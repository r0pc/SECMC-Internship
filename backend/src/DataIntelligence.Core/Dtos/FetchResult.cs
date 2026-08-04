using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Dtos;

/// <summary>
/// The outcome of one HTTP fetch. Failures are returned as data rather than thrown: the
/// scheduler must log and continue, never crash on a bad response (FR-2).
/// </summary>
public sealed record FetchResult
{
    public bool Succeeded { get; private init; }
    public string? Content { get; private init; }
    public string? ContentType { get; private init; }
    public short? HttpStatusCode { get; private init; }
    public CollectionFailureCategory? FailureCategory { get; private init; }
    public string? ErrorMessage { get; private init; }
    public string? ErrorDetail { get; private init; }

    /// <summary>How many attempts were made, including the successful one.</summary>
    public int Attempts { get; private init; }

    public static FetchResult Success(string content, string? contentType, short statusCode, int attempts) =>
        new()
        {
            Succeeded = true,
            Content = content,
            ContentType = contentType,
            HttpStatusCode = statusCode,
            Attempts = attempts
        };

    public static FetchResult Failure(
        CollectionFailureCategory category,
        string message,
        string? detail = null,
        short? statusCode = null,
        int attempts = 1) =>
        new()
        {
            Succeeded = false,
            FailureCategory = category,
            ErrorMessage = message,
            ErrorDetail = detail,
            HttpStatusCode = statusCode,
            Attempts = attempts
        };
}
