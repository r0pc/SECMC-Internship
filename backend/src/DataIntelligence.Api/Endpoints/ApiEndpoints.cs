using System.ComponentModel.DataAnnotations;
using System.Globalization;
using DataIntelligence.Core.Dtos;

namespace DataIntelligence.Api.Endpoints;

/// <summary>
/// Composition root for the HTTP surface (FR-7) and the conventions every endpoint shares.
/// </summary>
/// <remarks>
/// Everything lives under <c>/api</c>. Failures are returned as
/// <see href="https://datatracker.ietf.org/doc/html/rfc9457">ProblemDetails</see> so the
/// frontend has one error shape to handle rather than one per endpoint.
/// </remarks>
public static class ApiEndpoints
{
    /// <summary>Ceiling on series per trend request. More lines than this is an unreadable chart.</summary>
    public const int MaxTrendSeries = 10;

    /// <summary>Ceiling on series per KPI request — each costs two indexed seeks.</summary>
    public const int MaxKpiSeries = 20;

    public static IEndpointRouteBuilder MapDataIntelligenceApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapSourceEndpoints();
        api.MapCategoryEndpoints();
        api.MapSeriesEndpoints();
        api.MapDashboardEndpoints();
        api.MapCollectionEndpoints();

        return app;
    }

    /// <summary>Maps a write outcome onto the status code that describes it.</summary>
    internal static IResult ToHttpResult<T>(this WriteResult<T> result, Func<T, IResult> onSuccess) =>
        result.Outcome switch
        {
            WriteOutcome.Success => onSuccess(result.Value!),
            WriteOutcome.NotFound => Results.Problem(
                title: "Not found", detail: result.Message, statusCode: StatusCodes.Status404NotFound),
            WriteOutcome.Conflict => Results.Problem(
                title: "Conflict", detail: result.Message, statusCode: StatusCodes.Status409Conflict),
            WriteOutcome.InvalidReference => Results.Problem(
                title: "Invalid reference", detail: result.Message, statusCode: StatusCodes.Status400BadRequest),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };

    /// <summary>
    /// Validates a request body against its data annotations, returning a 400 with per-field
    /// errors or null when it is sound.
    /// </summary>
    internal static IResult? Validate<T>(T model) where T : notnull
    {
        var context = new ValidationContext(model);
        var results = new List<ValidationResult>();

        if (Validator.TryValidateObject(model, context, results, validateAllProperties: true))
        {
            return null;
        }

        var errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty),
                (r, member) => (Member: member, r.ErrorMessage))
            .GroupBy(e => e.Member)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage ?? "Invalid value.").ToArray());

        return Results.ValidationProblem(errors);
    }

    /// <summary>
    /// Parses a series-id list. Accepts <c>?seriesIds=1,2,3</c> and <c>?seriesIds=1&amp;seriesIds=2</c>
    /// alike — ASP.NET joins repeated values with commas before binding them to a string, so one
    /// parser covers both and the frontend can use whichever its HTTP client produces.
    /// </summary>
    internal static bool TryParseIds(
        string? raw,
        int maximum,
        out int[] ids,
        out string? error)
    {
        ids = [];
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "seriesIds is required — supply one or more ids, for example ?seriesIds=1,2.";
            return false;
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<int>(parts.Length);

        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                error = $"'{part}' is not a valid series id.";
                return false;
            }

            parsed.Add(id);
        }

        var distinct = parsed.Distinct().ToArray();

        if (distinct.Length == 0)
        {
            error = "seriesIds is required — supply one or more ids, for example ?seriesIds=1,2.";
            return false;
        }

        if (distinct.Length > maximum)
        {
            error = $"At most {maximum} series may be requested at once; {distinct.Length} were supplied.";
            return false;
        }

        ids = distinct;
        return true;
    }

    /// <summary>
    /// Rejects a backwards range. Left to the caller to fix rather than quietly swapped: a
    /// reversed range is a bug in the calling code, and swapping it hides the bug behind
    /// plausible-looking data.
    /// </summary>
    internal static IResult? ValidateRange(DateOnly? from, DateOnly? to) =>
        from is { } start && to is { } end && start > end
            ? Results.Problem(
                title: "Invalid date range",
                detail: $"'from' ({start:yyyy-MM-dd}) is after 'to' ({end:yyyy-MM-dd}).",
                statusCode: StatusCodes.Status400BadRequest)
            : null;

    internal static IResult BadRequest(string detail) =>
        Results.Problem(title: "Invalid request", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    internal static IResult NotFound(string detail) =>
        Results.Problem(title: "Not found", detail: detail, statusCode: StatusCodes.Status404NotFound);

    /// <summary>Keeps a rolling-window parameter inside a range the indexes are built for.</summary>
    internal static int ClampWindowDays(int? windowDays) => Math.Clamp(windowDays ?? 30, 1, 365);

    /// <summary>
    /// Reads a timestamp query parameter as UTC.
    /// </summary>
    /// <remarks>
    /// Every timestamp the platform stores is UTC, but a query string can arrive with an offset,
    /// with a <c>Z</c>, or with nothing at all. Without this, <c>?asOfUtc=2026-07-15T00:00:00</c>
    /// would be interpreted against the server's local zone — the same request answering
    /// differently depending on which machine served it.
    /// </remarks>
    internal static DateTime? NormalizeUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        { } v => v.ToUniversalTime()
    };
}
