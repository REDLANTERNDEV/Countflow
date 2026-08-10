using System.Text.Json.Serialization;
using Countflow.Api.Observability;
using Countflow.Api.OpenApi;
using Countflow.Bootstrap;
using Countflow.Modules.Identity;
using Countflow.Modules.Identity.Persistence;
using Countflow.Modules.MasterData.Persistence;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;

// Self-healthcheck mode. The runtime image is chiseled — no shell, no curl — so
// Docker's HEALTHCHECK re-invokes this same binary to probe the readiness
// endpoint rather than us giving up the hardened base image for a shell.
if (args.Contains("--healthcheck"))
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    var probeUrl = Environment.GetEnvironmentVariable("HEALTHCHECK_URL")
        ?? "http://localhost:8080/health/ready";

    try
    {
        var response = await probe.GetAsync(probeUrl);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.AddCountflowLogging();
builder.AddCountflowTelemetry();

builder.Services.AddCountflowModules(builder.Configuration, ModuleCatalog.All);
builder.Services.AddCountflowJwtBearer(builder.Configuration);

builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer(OpenApiSchemaTransformers.EnumsAsStrings);
    options.AddSchemaTransformer(OpenApiSchemaTransformers.NumbersAreNotStrings);
});

// Enums travel as names on the wire, matching what the OpenAPI document now
// advertises and keeping request bodies readable.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails();

// Readiness reports whether the API can actually serve: it can only do that
// once every module's schema is reachable. Liveness stays trivially cheap so a
// slow database never gets the container killed and restarted in a loop.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<IdentityModuleDbContext>("identity-db", tags: ["ready"])
    .AddDbContextCheck<MasterDataDbContext>("masterdata-db", tags: ["ready"]);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Always mapped: the OpenAPI document is a published product surface, and it is
// what the TypeScript client is generated from. The browsable UI stays dev-only.
app.MapOpenApi();

if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapCountflowModules();

if (args is ["--export-openapi", var exportPath, ..])
{
    return await OpenApiExporter.ExportAsync(app, exportPath);
}

app.Run();

return 0;

/// <summary>Exposed so integration tests can drive the real host via WebApplicationFactory.</summary>
public partial class Program;
