using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;

namespace DataIntelligence.Api.Endpoints;

/// <summary>
/// The series catalogue and the observations belonging to a series (FR-7, FR-10, FR-11).
/// </summary>
public static class SeriesEndpoints
{
    public static RouteGroupBuilder MapSeriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/series").WithTags("Series");

        group.MapGet("/", async (
                byte? dataSourceId,
                int? categoryId,
                SeriesFrequency? frequency,
                SeasonalAdjustment? seasonalAdjustment,
                bool? isActive,
                string? search,
                bool? includeLatest,
                int? page,
                int? pageSize,
                ICatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var query = new SeriesQuery
                {
                    DataSourceId = dataSourceId,
                    CategoryId = categoryId,
                    Frequency = frequency,
                    SeasonalAdjustment = seasonalAdjustment,
                    // Active-only unless asked otherwise: a deactivated series is hidden from
                    // dashboards by definition, and defaulting the other way would quietly put it
                    // back on every chart that lists series.
                    IsActive = isActive ?? true,
                    Search = search,
                    IncludeLatest = includeLatest ?? true,
                    Page = PageRequest.Normalize(page, pageSize)
                };

                return Results.Ok(await catalog.GetSeriesAsync(query, cancellationToken));
            })
            .WithName("GetSeries")
            .WithSummary("Lists series, filtered and paged.")
            .WithDescription(
                "Defaults to active series with their latest value attached. Pass isActive=false "
                + "for deactivated ones, or includeLatest=false to skip the value lookup when only "
                + "names are needed.")
            .Produces<PagedResult<SeriesDto>>();

        group.MapGet("/{seriesId:int}", async (
                int seriesId,
                ICatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var series = await catalog.GetSeriesByIdAsync(seriesId, cancellationToken);

                return series is null
                    ? ApiEndpoints.NotFound($"No series with id {seriesId}.")
                    : Results.Ok(series);
            })
            .WithName("GetSeriesById")
            .WithSummary("Reads one series, including its latest value and concurrency token.")
            .Produces<SeriesDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{seriesId:int}", async (
                int seriesId,
                SeriesUpdateRequest request,
                ICatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var invalid = ApiEndpoints.Validate(request);

                if (invalid is not null)
                {
                    return invalid;
                }

                var result = await catalog.UpdateSeriesAsync(seriesId, request, cancellationToken);

                return result.ToHttpResult(Results.Ok);
            })
            .WithName("UpdateSeries")
            .WithSummary("Updates a series' presentation fields.")
            .WithDescription(
                "Title, category, decimal places, and active state. Everything that describes the "
                + "data itself — code, unit, frequency, seasonal adjustment — belongs to the "
                + "publisher and stays read-only. Send back the rowVersion you read to be told "
                + "about a concurrent edit (409) instead of silently overwriting it.")
            .Produces<SeriesDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{seriesId:int}/observations", async (
                int seriesId,
                DateOnly? from,
                DateOnly? to,
                PeriodType? periodType,
                bool? includeRevisions,
                DateTime? asOfUtc,
                SortDirection? sort,
                int? page,
                int? pageSize,
                IDashboardQueryService dashboard,
                CancellationToken cancellationToken) =>
            {
                var badRange = ApiEndpoints.ValidateRange(from, to);

                if (badRange is not null)
                {
                    return badRange;
                }

                var query = new ObservationQuery
                {
                    SeriesId = seriesId,
                    From = from,
                    To = to,
                    PeriodType = periodType,
                    IncludeRevisions = includeRevisions ?? false,
                    AsOfUtc = ApiEndpoints.NormalizeUtc(asOfUtc),
                    Sort = sort ?? SortDirection.Ascending,
                    Page = PageRequest.Normalize(page, pageSize, PageRequest.ObservationPageSizeLimit)
                };

                var result = await dashboard.GetObservationsAsync(query, cancellationToken);

                return result is null
                    ? ApiEndpoints.NotFound($"No series with id {seriesId}.")
                    : Results.Ok(result);
            })
            .WithName("GetObservations")
            .WithSummary("Reads one series' observations over a date range.")
            .WithDescription(
                "Read-only by design: observations are append-only (FR-4) and written solely by "
                + "the collector, which is what makes the historical record trustworthy.\n\n"
                + "Defaults to current values in the series' own period length, so a BLS "
                + "annual-average row (M13, stored as PeriodType=Annual) cannot land on a monthly "
                + "chart as a thirteenth month. Override with periodType to read those rows.\n\n"
                + "includeRevisions=true adds superseded vintages. asOfUtc reads the values the "
                + "platform held at that instant — 'what did we believe June's CPI was, on 15 "
                + "July?' — and supersedes includeRevisions, since a point in time has exactly "
                + "one vintage per period.")
            .Produces<PagedResult<ObservationDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }
}
