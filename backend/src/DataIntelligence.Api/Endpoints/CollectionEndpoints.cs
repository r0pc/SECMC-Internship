using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;

namespace DataIntelligence.Api.Endpoints;

/// <summary>
/// The collection log and its health metrics (FR-2, NFR Reliability).
/// </summary>
/// <remarks>
/// Every cycle is recorded, including the ones that fail, so an operations panel can show that
/// the platform is collecting — or say precisely why it is not. A dashboard whose numbers stop
/// updating looks identical to one whose numbers have not changed; this is the difference.
/// </remarks>
public static class CollectionEndpoints
{
    public static RouteGroupBuilder MapCollectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/collection").WithTags("Collection");

        group.MapGet("/runs", async (
                byte? dataSourceId,
                CollectionRunStatus? status,
                bool? failuresOnly,
                DateTime? fromUtc,
                DateTime? toUtc,
                int? page,
                int? pageSize,
                IDashboardQueryService dashboard,
                CancellationToken cancellationToken) =>
            {
                var from = ApiEndpoints.NormalizeUtc(fromUtc);
                var to = ApiEndpoints.NormalizeUtc(toUtc);

                if (from is { } start && to is { } end && start > end)
                {
                    return ApiEndpoints.BadRequest($"'fromUtc' ({start:O}) is after 'toUtc' ({end:O}).");
                }

                var query = new CollectionRunQuery
                {
                    DataSourceId = dataSourceId,
                    Status = status,
                    FailuresOnly = failuresOnly ?? false,
                    FromUtc = from,
                    ToUtc = to,
                    Page = PageRequest.Normalize(page, pageSize)
                };

                return Results.Ok(await dashboard.GetCollectionRunsAsync(query, cancellationToken));
            })
            .WithName("GetCollectionRuns")
            .WithSummary("Lists collection runs, newest first.")
            .WithDescription(
                "Filter by source, status, or start time. failuresOnly=true narrows to failed and "
                + "partial runs — the operations panel's default view.")
            .Produces<PagedResult<CollectionRunDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/runs/{collectionRunId:long}", async (
                long collectionRunId,
                IDashboardQueryService dashboard,
                CancellationToken cancellationToken) =>
            {
                var run = await dashboard.GetCollectionRunAsync(collectionRunId, cancellationToken);

                return run is null
                    ? ApiEndpoints.NotFound($"No collection run with id {collectionRunId}.")
                    : Results.Ok(run);
            })
            .WithName("GetCollectionRun")
            .WithSummary("Reads one collection run.")
            .Produces<CollectionRunDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/health", async (
                int? windowDays,
                IDashboardQueryService dashboard,
                CancellationToken cancellationToken) =>
            {
                var health = await dashboard.GetHealthAsync(
                    ApiEndpoints.ClampWindowDays(windowDays), cancellationToken);

                return Results.Ok(health);
            })
            .WithName("GetCollectionHealth")
            .WithSummary("Per-source collection health over a rolling window.")
            .WithDescription(
                "successRatePercent counts succeeded and partial runs over completed attempts; "
                + "runs still in flight and skipped cycles are excluded, so the figure means what "
                + "the SOW's ≥99% target measures. It is null — not 100 — when the window holds no "
                + "runs at all.\n\n"
                + "consecutiveFailures is the count of failures ending at the most recent run, "
                + "capped at 20. Non-zero means collection is broken now, rather than having been "
                + "broken at some point in the window.")
            .Produces<IReadOnlyList<SourceHealthDto>>();

        return group;
    }
}
