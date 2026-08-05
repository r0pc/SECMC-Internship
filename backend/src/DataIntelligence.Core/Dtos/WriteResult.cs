namespace DataIntelligence.Core.Dtos;

/// <summary>Why a write succeeded or did not, mapped to a status code by the endpoint layer.</summary>
public enum WriteOutcome
{
    Success,

    /// <summary>The target row does not exist — 404.</summary>
    NotFound,

    /// <summary>
    /// The request is well-formed but conflicts with the current state: a duplicate code, a
    /// concurrent edit, or a delete blocked by rows that reference the target — 409.
    /// </summary>
    Conflict,

    /// <summary>A referenced row (parent category, source) does not exist — 400.</summary>
    InvalidReference
}

/// <summary>
/// The result of a write, returned as data rather than signalled with exceptions.
/// </summary>
/// <remarks>
/// Exceptions for "not found" and "already exists" would make the ordinary outcomes of a CRUD
/// endpoint cost a stack trace apiece and push the status-code decision into a filter. Returning
/// the outcome keeps the mapping visible in the endpoint that owns the contract.
/// </remarks>
public sealed record WriteResult<T>
{
    public required WriteOutcome Outcome { get; init; }

    /// <summary>The written row on success; null otherwise.</summary>
    public T? Value { get; init; }

    /// <summary>Human-readable detail, surfaced in the ProblemDetails body.</summary>
    public string? Message { get; init; }

    public bool Succeeded => Outcome == WriteOutcome.Success;

    public static WriteResult<T> Success(T value) =>
        new() { Outcome = WriteOutcome.Success, Value = value };

    public static WriteResult<T> NotFound(string message) =>
        new() { Outcome = WriteOutcome.NotFound, Message = message };

    public static WriteResult<T> Conflict(string message) =>
        new() { Outcome = WriteOutcome.Conflict, Message = message };

    public static WriteResult<T> InvalidReference(string message) =>
        new() { Outcome = WriteOutcome.InvalidReference, Message = message };
}
