using System.Text.Json.Serialization;
using Countflow.Api.Errors;
using Countflow.Api.Observability;
using Countflow.Api.OpenApi;
using Countflow.Api.RateLimiting;
using Countflow.Bootstrap;
using Countflow.Modules.Identity;
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
builder.Services.AddCountflowAuthentication(builder.Configuration);
builder.Services.AddCountflowRateLimiting(builder.Configuration);

builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer(OpenApiSchemaTransformers.EnumsAsStrings);
    options.AddSchemaTransformer(OpenApiSchemaTransformers.NumbersAreNotStrings);
    options.AddDocumentTransformer(OpenApiDocumentTransformers.DescribeTheSurface);
    options.AddDocumentTransformer(OpenApiDocumentTransformers.DescribeSecurity);
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enums travel as names on the wire, matching what the OpenAPI document now
    // advertises and keeping request bodies readable.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // Make the request contracts mean what the published document already says
    // they mean. `record LoginRequest(string Identifier, string Password)` reads
    // as two required strings and is exported as `"required": ["identifier",
    // "password"]` — but System.Text.Json's defaults ignore both halves of that,
    // leaving `Identifier` null for a body that omitted it or sent an explicit
    // null. Every handler downstream then trusts its non-nullable parameter, and
    // the first `.Trim()` turns an integrator's typo into `http.unexpected_error`
    // and a stack trace in our logs. Seven endpoints across five modules did
    // exactly that; `docs/api/error-codes.md` has always promised
    // `http.malformed_request` for it, and a missing field is now refused during
    // binding, before any handler sees the request.
    //
    // Cheap to hold: every optional parameter in this codebase already carries a
    // default, so these two options require exactly the fields the OpenAPI
    // document already marks required, and the exported schema does not move.
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.RespectNullableAnnotations = true;
});

// A body that will not bind answers `400` in every environment, which it did not.
//
// `RouteHandlerOptions.ThrowOnBadRequest` defaults to on under Development, and it
// does not merely log louder: the binding failure is rethrown as a
// `BadHttpRequestException`, `UseExceptionHandler` catches it, discards the `400`
// it carries, and writes `http.unexpected_error`. So the `http.malformed_request`
// that `docs/api/error-codes.md` promises had never actually been emitted by the
// stack anyone runs — malformed JSON, a mistyped field and a missing one were all
// 500s under `docker compose up`, and only correct in production, where nobody
// looks first.
//
// Turned off rather than corrected downstream, deliberately: with the throw off,
// Development takes the identical path production takes — the factory sets the
// status and `UseStatusCodePages` writes the coded document — instead of a second
// mapping beside it that has to be kept agreeing with the first. The failure is
// still logged, at Debug, and the request log carries the 400; an ERR with a stack
// trace was the wrong severity for a caller's typo anyway.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

// Every problem document this host emits carries a `code`, including the ones the
// framework writes for itself — a bad credential, a missing role, a mistyped path.
// See TransportErrorCodes for why that turned out to matter.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = TransportErrorCodes.Apply);

// Readiness reports whether the API can actually serve: it can only do that
// once every module's schema is reachable. Liveness stays trivially cheap so a
// slow database never gets the container killed and restarted in a loop.
//
// One check over every module context rather than a line per module, so adding a
// module really is the single line in ModuleCatalog it is advertised to be.
builder.Services.AddHealthChecks()
    .AddCheck<ModuleHealthCheck>("module-schemas", tags: ["ready"]);

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

// Before authentication, deliberately. The limiter partitions on the credential a
// caller *presented* rather than on the identity it turned out to belong to, so a
// flood of forged API keys is throttled without any of them reaching the database
// — see RateLimitingExtensions.Credential.
if (builder.Configuration.GetValue($"{RateLimitingOptions.SectionName}:Enabled", defaultValue: true))
{
    app.UseRateLimiter();
}

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
