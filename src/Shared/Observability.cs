using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RelayStack;

/// <summary>
/// Structured JSON logs to stdout, plus OpenTelemetry traces and metrics exported over OTLP when
/// OTEL_EXPORTER_OTLP_ENDPOINT is set. ASP.NET Core, HttpClient and Npgsql emit their own activities and
/// meters in .NET 10, so no instrumentation packages are needed.
/// </summary>
internal static class Observability
{
    public static void AddRelayStackObservability(this IServiceCollection services, ILoggingBuilder logging, IConfiguration configuration, string serviceName)
    {
        logging.ClearProviders();
        logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        });

        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddSource("RelayStack.*", "Microsoft.AspNetCore", "System.Net.Http", "Npgsql"))
            .WithMetrics(m => m
                .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Net.Http", "Npgsql", "RelayStack.*"));

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            otel.WithTracing(t => t.AddOtlpExporter()).WithMetrics(m => m.AddOtlpExporter());
        }
    }
}
