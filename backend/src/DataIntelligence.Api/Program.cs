var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// The frontend is deployed independently of the API (SOW 4.2), so it needs an explicit origin allowance.
const string FrontendCorsPolicy = "FrontendCorsPolicy";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod());
});

// TODO (Phase 4): register DbContext, collection services, AI orchestration, and authentication.

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Serves the OpenAPI document at /openapi/v1.json - the frontend generates its
    // TypeScript types from this - and a browsable UI over it at /swagger.
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Data Intelligence API v1"));
}

app.UseHttpsRedirection();
app.UseCors(FrontendCorsPolicy);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// TODO (Phase 4): map dashboard, reporting, and AI assistant endpoints.

app.Run();

/// <summary>Exposed so integration tests can host the API via <c>WebApplicationFactory</c>.</summary>
public partial class Program { }
