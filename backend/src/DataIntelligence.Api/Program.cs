using System.Text.Json.Serialization;
using DataIntelligence.Api.Endpoints;
using DataIntelligence.Api.Json;
using DataIntelligence.Infrastructure;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

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

// TODO (Phase 4): register AI orchestration (FR-13 - FR-16) and authentication (FR-9).

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

// Liveness plus database reachability. A process that is up but cannot reach SQL Server serves
// nothing but errors, so reporting it healthy would tell a load balancer exactly the wrong thing.
app.MapGet("/health", async (DataIntelligenceDbContext db, CancellationToken cancellationToken) =>
    {
        var databaseReachable = await db.Database.CanConnectAsync(cancellationToken);

        return databaseReachable
            ? Results.Ok(new { status = "ok", database = "ok" })
            : Results.Json(
                new { status = "degraded", database = "unreachable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    })
    .WithTags("Health")
    .WithName("GetHealth")
    .WithSummary("Liveness and database reachability.");

app.MapDataIntelligenceApi();

app.Run();

/// <summary>Exposed so integration tests can host the API via <c>WebApplicationFactory</c>.</summary>
public partial class Program { }

