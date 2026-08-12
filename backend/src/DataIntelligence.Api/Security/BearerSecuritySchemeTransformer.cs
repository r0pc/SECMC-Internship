using Microsoft.AspNetCore.OpenApi;

// Microsoft.OpenApi 2.x moved the document model into the root namespace; there is no
// Microsoft.OpenApi.Models any more, which is what most examples still import.
using Microsoft.OpenApi;

namespace DataIntelligence.Api.Security;

/// <summary>
/// Declares the bearer scheme in the OpenAPI document (FR-9).
/// </summary>
/// <remarks>
/// ASP.NET's document generator describes routes, parameters and payloads; it does not know that
/// the endpoints behind them need a token. Without this the published contract would describe an
/// API anyone could call, the Swagger UI would have no way to send a token, and every generated
/// client would be built to make unauthenticated requests.
/// </remarks>
public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public const string SchemeName = "Bearer";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description =
                "The access token from POST /api/auth/login, sent as 'Authorization: Bearer "
                + "<token>'. Required by every endpoint except that one and /health."
        };

        // Declared once for the whole document rather than per operation. Every endpoint requires
        // it — that is what makes the two exceptions worth stating in prose instead of leaving the
        // reader to diff a list of operations.
        document.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SchemeName, document)] = []
            }
        ];

        return Task.CompletedTask;
    }
}
