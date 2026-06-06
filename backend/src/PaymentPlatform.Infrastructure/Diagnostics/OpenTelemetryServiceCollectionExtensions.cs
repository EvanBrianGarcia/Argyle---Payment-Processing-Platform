using MassTransit.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PaymentPlatform.Application.Diagnostics;

namespace PaymentPlatform.Infrastructure.Diagnostics;

public static class OpenTelemetryServiceCollectionExtensions
{
    /// Registers the OpenTelemetry tracing SDK with auto-instrumentation for
    /// ASP.NET Core, HttpClient, Npgsql, and MassTransit. Console exporter is
    /// always-on; OTLP exporter is registered only when
    /// `OpenTelemetry:Otlp:Endpoint` is set.
    ///
    /// `configureTracing` receives the TracerProviderBuilder *after* the
    /// shared registration runs — the API and Worker share a single
    /// registration shape and only differ in their service name.
    public static IServiceCollection AddPaymentsTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        var otlpEndpoint = configuration["OpenTelemetry:Otlp:Endpoint"];

        services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName)
                .AddEnvironmentVariableDetector())
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new AlwaysOnSampler())
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.Filter = ctx =>
                        {
                            var path = ctx.Request.Path;
                            return !path.StartsWithSegments("/health")
                                && !path.StartsWithSegments("/metrics");
                        };
                    })
                    .AddHttpClientInstrumentation()
                    .AddSource("Npgsql")
                    .AddSource(DiagnosticHeaders.DefaultListenerName)
                    .AddSource(PaymentsActivitySource.Name)
                    .AddConsoleExporter();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }

                configureTracing?.Invoke(tracing);
            });

        return services;
    }
}
