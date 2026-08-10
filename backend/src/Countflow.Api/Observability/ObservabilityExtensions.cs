using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace Countflow.Api.Observability;

public static class ObservabilityExtensions
{
    public const string ServiceName = "countflow-api";

    /// <summary>
    /// Serilog to console (structured JSON in production) and to Seq when a
    /// server URL is configured. Configured on the host so startup itself is
    /// logged, not just requests.
    /// </summary>
    public static void AddCountflowLogging(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Service", ServiceName)
                .WriteTo.Console();

            var seqUrl = builder.Configuration["Observability:SeqUrl"];
            if (!string.IsNullOrWhiteSpace(seqUrl))
            {
                configuration.WriteTo.Seq(seqUrl);
            }
        });
    }

    public static void AddCountflowTelemetry(this IHostApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["Observability:OtlpEndpoint"];

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // Health probes fire constantly and would drown the traces.
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
                }
            });
    }
}
