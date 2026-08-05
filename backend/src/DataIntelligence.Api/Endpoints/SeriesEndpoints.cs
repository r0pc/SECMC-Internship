using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;

namespace DataIntelligence.Api.Endpoints;

/// <summary>
/// The series catalogue and the rows behind each series (FR-7, FR-10, FR-11).
/// </summary>
/// <remarks>
/// Read-only. A series is a fixed measure of a fixed dataset — CPI's one BLS series, or one
/// column of a SOFR business day — so there is nothing here a caller could edit that would not
/// simply make the platform disagree with its own schema.
/// </remarks>
public static class SeriesEndpoints
{
    public static RouteGroupBuilder MapSeriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/series").WithTags("Series");

        group.MapGet("/", async (
                byte? dataSourceId,
                Dataset? dataset,
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
                    Dataset = dataset,
                    Search = search,
                    IncludeLatest = includeLatest ?? true,
                    Page = PageRequest.Normalize(page, pageSize)
                };

                return Results.Ok(await catalog.GetSeriesAsync(query, cancellationToken));
            })
            .WithName("GetSeries")
            .WithSummary("Lists the series a chart can draw.")
            .WithDescription(
                "Seven entries: CPI, and SOFR's rate, volume and four percentiles. Each carries "
                + "its latest value unless includeLatest=false.\n\n"
                + "Units are not interchangeable and are returned per series — SOFR volume is in "
                + "billions of dollars and CPI is an index. Values are stored exactly as "
                + "published and never rescaled, so a chart that ignores the unit will look fine "
                + "while being nonsense.")
            .Produces<PagedResult<SeriesDto>>();

        group.MapGet("/{seriesKey}", async (
                string seriesKey,
                ICatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var series = await catalog.GetSeriesByKeyAsync(seriesKey, cancellationToken);

                return series is null
                    ? ApiEndpoints.NotFound($"No series with key '{seriesKey}'.")
                    : Results.Ok(series);
            })
            .WithName("GetSeriesByKey")
            .WithSummary("Reads one series, including its latest value.")
            .Produces<SeriesDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{seriesKey}/observations", async (
                string seriesKey,
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
                    SeriesKey = seriesKey,
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
                    ? ApiEndpoints.NotFound($"No series with key '{seriesKey}'.")
                    : Results.Ok(result);
            })
            .WithName("GetObservations")
            .WithSummary("Reads one series' stored values over a date range.")
            .WithDescription(
                "Read-only by design: both fact tables are append-only (FR-4) and written solely "
                + "by the collector, which is what makes the historical record trustworthy.\n\n"
                + "For CPI, defaults to monthly figures, so the annual average BLS publishes "
                + "alongside them (period code M13) cannot land on a monthly chart as a "
                + "thirteenth month. Pass periodType=Annual or Semiannual to read those instead. "
                + "Ignored for SOFR, where every row is one business day.\n\n"
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
