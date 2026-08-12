using System.Text.Json.Serialization;
using DataIntelligence.Api.Endpoints;
using DataIntelligence.Api.Json;
using DataIntelligence.Api.Security;
using DataIntelligence.Infrastructure;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// The transformer adds the bearer scheme; without it the published contract would describe an API
// that needs no credentials (FR-9).
builder.Services.AddOpenApi(options =>
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

// Throws at startup when the connection string is missing, rather than at the first request.
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddAnalytics();

// One error shape for the whole API (RFC 9457), including unhandled exceptions.
builder.Services.AddProblemDetails();

// A malformed JSON body is the caller's mistake, and must be answered 400 — not 500.
// Minimal APIs default ThrowOnBadRequest to true in Development, which turns a binding failure
// into an exception that UseExceptionHandler then reports as an unhandled server error. The
// result is an API that answers the same bad request differently depending on the environment
// it is running in, and blames itself for the caller's typo.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enums as their names. The frontend gets "Monthly" rather than 3, and the OpenAPI document
    // lists the permitted values — which is what lets its generated types be useful.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // Timestamps with an explicit Z. See UtcDateTimeConverter for why this is not optional.
    options.SerializerOptions.Converters.Add(new UtcDateTimeConverter());
});

builder.Services.AddAssistant(builder.Configuration);

// The frontend is deployed independently of the API (SOW 4.2), so it needs an explicit origin allowance.
const string FrontendCorsPolicy = "FrontendCorsPolicy";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod());
});

// Accounts, password hashing and token issuance (FR-9). Validates its signing key at startup:
// every endpoint below requires a token, so a process that boots without one can serve nothing.
builder.Services.AddSecurity(builder.Configuration);
builder.Services.AddPlatformAuthentication();
builder.Services.AddAuthorizationBuilder().AddPlatformPolicies();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    // Serves the OpenAPI document at /openapi/v1.json - the frontend generates its
    // TypeScript types from this - and a browsable UI over it at /swagger.
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Data Intelligence API v1"));
}

app.UseHttpsRedirection();
app.UseCors(FrontendCorsPolicy);

// Order matters and is not interchangeable: authentication works out who the caller is, and
// authorization then decides whether that caller may have what they asked for. Both sit after
// CORS so a rejected cross-origin request is answered as one rather than as a 401.
app.UseAuthentication();
app.UseAuthorization();

// Liveness plus database reachability. A process that is up but cannot reach SQL Server serves
// nothing but errors, so reporting it healthy would tell a load balancer exactly the wrong thing.
//
// Anonymous, deliberately: a load balancer cannot sign in, and what this reports — up, and can it
// see the database — is not information worth a credential. It is the only endpoint outside the
// /api group, which is where FR-9's blanket requirement is declared.
app.MapGet("/health", async (DataIntelligenceDbContext db, CancellationToken cancellationToken) =>
    {
        var databaseReachable = await db.Database.CanConnectAsync(cancellationToken);

        return databaseReachable
            ? Results.Ok(new { status = "ok", database = "ok" })
            : Results.Json(
                new { status = "degraded", database = "unreachable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    })
    .AllowAnonymous()
    .WithTags("Health")
    .WithName("GetHealth")
    .WithSummary("Liveness and database reachability.");

app.MapDataIntelligenceApi();

app.Run();

/// <summary>Exposed so integration tests can host the API via <c>WebApplicationFactory</c>.</summary>
public partial class Program { }

