using Prometheus;

namespace PaymentPlatform.Api.Endpoints;

public static class MetricsEndpoint
{
    /// Maps `/metrics` to prometheus-net's default registry. The route is
    /// intentionally at the root (not under `/v1`) because Prometheus
    /// scrapers conventionally hit `/metrics` directly — see ADR-0012.
    public static IEndpointRouteBuilder MapMetricsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapMetrics("/metrics");
        return app;
    }
}
