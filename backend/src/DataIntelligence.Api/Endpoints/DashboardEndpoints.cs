using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;

namespace DataIntelligence.Api.Endpoints;

/// <summary>
/// The aggregates the dashboards render: summary, KPI tiles, and trend lines (FR-10, FR-11).
/// </summary>
/// <remarks>
/// Every number here is computed by SQL Server. The endpoints exist so the frontend fetches one
/// small payload per panel instead of paging raw observations and aggregating in the browser —
/// which is what keeps a 12-month range inside the 3-second target (NFR Performance).
/// </remarks>
public static class DashboardEndpoints
{
    public static RouteGroupBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/dashboard").WithTags("Dashboard");

        group.MapGet("/summary", async (
                int? windowDays,
                IDashboardQueryService dashboard,
                CancellationToken cancellationToken) =>
            {
                var summary = await dashboard.GetSummaryAsync(
                    ApiEndpoints.ClampWindowDays(windowDays), cancellationToken);

                return Results.Ok(summary);
            })
            .WithName("GetDashboardSummary")
            .WithSummary("Everything the landing page needs in one request.")
            .WithDescription(
                "Catalogue counts, the span of stored history per dataset, and per-source "
                + "collection health over a rolling window (30 days by default, clamped to "
                + "1–365).\n\n"
                + "Row counts are reported per dataset rather than summed: a CPI row is a month "
                + "and a SOFR row is a business day, so one total would only invite the reader to "
                + "compare them.")
            .Produces<DashboardSummaryDto>();

        group.MapGet("/kpis", async (
                string? seriesKeys,
                IDashboardQueryService dashboard,
                CancellationToken cancellationToken) =>
            {
                if (!ApiEndpoints.TryParseSeriesKeys(
                        seriesKeys, ApiEndpoints.MaxKpiSeries, out var keys, out var error))
                {
                    return ApiEndpoints.BadRequest(error!);
                }

                return Results.Ok(await dashboard.GetKpisAsync(keys, cancellationToken));
            })
            .WithName("GetKpis")
            .WithSummary("Headline numbers for the requested series.")
            .WithDescription(
                "Latest value, the change since the previous release, and the change since a year "
                + "ago — for CPI, that last one is the inflation rate as normally quoted.\n\n"
                + "The year-ago comparison matches the most recent release at or before one year "
                + "prior rather than an exact date: SOFR does not publish on weekends, so an exact "
                + "lookup would come back empty about two days in seven.\n\n"
                + "Accepts ?seriesKeys=cpi,sofr or repeated ?seriesKeys=. At most "
                + $"{ApiEndpoints.MaxKpiSeries} series per request; unknown keys are skipped.")
            .Produces<IReadOnlyList<SeriesKpiDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/trend", async (
                string? seriesKeys,
                DateOnly? from,
                DateOnly? to,
                TrendGranularity? granularity,
                IDashboardQueryService dashboard,
                CancellationToken cancellationToken) =>
            {
                if (!ApiEndpoints.TryParseSeriesKeys(
                        seriesKeys, ApiEndpoints.MaxTrendSeries, out var keys, out var error))
                {
                    return ApiEndpoints.BadRequest(error!);
                }

                var badRange = ApiEndpoints.ValidateRange(from, to);

                if (badRange is not null)
                {
                    return badRange;
                }

                var query = new TrendQuery
                {
                    SeriesKeys = keys,
                    From = from,
                    To = to,
                    Granularity = granularity ?? TrendGranularity.Auto
                };

                return Results.Ok(await dashboard.GetTrendAsync(query, cancellationToken));
            })
            .WithName("GetTrend")
            .WithSummary("Trend lines for the requested series over a shared range.")
            .WithDescription(
                "Defaults to the last twelve months. Granularity defaults to Auto, which keeps "
                + "points unbucketed until the range would produce more of them than a chart can "
                + "usefully draw, then widens to Month, Quarter, or Year.\n\n"
                + "Where a bucket holds several observations, value is the mean and "
                + "minimum/maximum carry the spread, so a chart can draw a band instead of "
                + "implying the average was the whole story. The bucket width actually used comes "
                + "back on each line.\n\n"
                + "Units are returned per series and are not interchangeable — SOFR volume is in "
                + "billions of dollars and CPI is an index; plotting them on one axis is "
                + $"meaningless. At most {ApiEndpoints.MaxTrendSeries} series per request.")
            .Produces<IReadOnlyList<TrendSeriesDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return group;
    }
}
