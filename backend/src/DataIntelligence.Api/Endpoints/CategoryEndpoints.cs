using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Interfaces;

namespace DataIntelligence.Api.Endpoints;

/// <summary>
/// Series categories — the platform's own grouping, and the only fully CRUD resource in the
/// API (FR-7, FR-11).
/// </summary>
public static class CategoryEndpoints
{
    public static RouteGroupBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/categories").WithTags("Categories");

        group.MapGet("/", async (ICatalogService catalog, CancellationToken cancellationToken) =>
                Results.Ok(await catalog.GetCategoriesAsync(cancellationToken)))
            .WithName("GetCategories")
            .WithSummary("Lists categories in sort order.")
            .WithDescription(
                "Returned flat. The hierarchy is in parentCategoryId — CPI's item structure is a "
                + "tree (All items → Food and beverages → Food), and a flat list lets the caller "
                + "build whichever shape its drill-down needs.")
            .Produces<IReadOnlyList<SeriesCategoryDto>>();

        group.MapGet("/{categoryId:int}", async (
                int categoryId,
                ICatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var category = await catalog.GetCategoryAsync(categoryId, cancellationToken);

                return category is null
                    ? ApiEndpoints.NotFound($"No category with id {categoryId}.")
                    : Results.Ok(category);
            })
            .WithName("GetCategory")
            .WithSummary("Reads one category.")
            .Produces<SeriesCategoryDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
                SeriesCategoryCreateRequest request,
                ICatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var invalid = ApiEndpoints.Validate(request);

                if (invalid is not null)
                {
                    return invalid;
                }

                var result = await catalog.CreateCategoryAsync(request, cancellationToken);

                return result.ToHttpResult(created =>
                    Results.Created($"/api/categories/{created.CategoryId}", created));
            })
            .WithName("CreateCategory")
            .WithSummary("Creates a category.")
            .Produces<SeriesCategoryDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{categoryId:int}", async (
                int categoryId,
                SeriesCategoryUpdateRequest request,
                ICatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var invalid = ApiEndpoints.Validate(request);

                if (invalid is not null)
                {
                    return invalid;
                }

                var result = await catalog.UpdateCategoryAsync(categoryId, request, cancellationToken);

                return result.ToHttpResult(Results.Ok);
            })
            .WithName("UpdateCategory")
            .WithSummary("Renames or re-parents a category.")
            .WithDescription(
                "The code is immutable: it is how configuration and saved dashboard views refer to "
                + "the category. A move that would make the category its own ancestor is refused.")
            .Produces<SeriesCategoryDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{categoryId:int}", async (
                int categoryId,
                ICatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var result = await catalog.DeleteCategoryAsync(categoryId, cancellationToken);

                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteCategory")
            .WithSummary("Deletes an empty category.")
            .WithDescription(
                "Refused while series or child categories still reference it. Reassign them first — "
                + "the alternative is orphaning rows the caller cannot see from here.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }
}
