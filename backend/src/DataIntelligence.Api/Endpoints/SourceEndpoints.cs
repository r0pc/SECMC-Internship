using DataIntelligence.Api.Security;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Interfaces;

namespace DataIntelligence.Api.Endpoints;

/// <summary>Data sources: the publishers the platform collects from (FR-7).</summary>
public static class SourceEndpoints
{
    public static RouteGroupBuilder MapSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/sources").WithTags("Sources");

        group.MapGet("/", async (ICatalogService catalog, CancellationToken cancellationToken) =>
                Results.Ok(await catalog.GetSourcesAsync(cancellationToken)))
            .WithName("GetSources")
            .WithSummary("Lists every data source with its active series count.")
            .Produces<IReadOnlyList<DataSourceDto>>();

        group.MapGet("/{dataSourceId:int}", async (
                byte dataSourceId,
                ICatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var source = await catalog.GetSourceAsync(dataSourceId, cancellationToken);

                return source is null
                    ? ApiEndpoints.NotFound($"No data source with id {dataSourceId}.")
                    : Results.Ok(source);
            })
            .WithName("GetSource")
            .WithSummary("Reads one data source.")
            .Produces<DataSourceDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{dataSourceId:int}", async (
                byte dataSourceId,
                DataSourceUpdateRequest request,
                ICatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var invalid = ApiEndpoints.Validate(request);

                if (invalid is not null)
                {
                    return invalid;
                }

                var result = await catalog.UpdateSourceAsync(dataSourceId, request, cancellationToken);

                return result.ToHttpResult(Results.Ok);
            })
            // Administrator only. Everything else in this file is a read that any signed-in user
            // may make; this one can switch off collection from a publisher, which is the whole
            // platform quietly going stale.
            .RequireAuthorization(AuthorizationPolicies.Administer)
            .WithName("UpdateSource")
            .WithSummary("Updates the polling settings of a source.")
            .WithDescription(
                "Administrator only. Only fields the platform owns are editable — enabled state, "
                + "interval, timeout, retries, user agent, terms-of-use link. Endpoint, HTTP "
                + "method and access method are fixed by the adapter compiled against that "
                + "publisher, so changing them here could only break collection. Omitted fields "
                + "are left unchanged.")
            .Produces<DataSourceDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }
}
