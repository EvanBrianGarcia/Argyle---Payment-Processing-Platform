using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using PaymentPlatform.Application.Diagnostics;

namespace PaymentPlatform.IntegrationTests.Fixtures;

/// Wires a process-wide TracerProvider that exports captured spans into a
/// list the tests own. Subscribes to every ActivitySource the production
/// code is expected to populate so RED tests can prove the wiring is missing
/// (no app-level spans yet, no /health filter yet) while GREEN tests can
/// assert spans + cross-process traceparent linkage land where expected.
///
/// One TracerProvider per fixture instance. xUnit shares the instance across
/// every test in the class via IClassFixture, so the captured list is
/// long-lived — each test snapshots/filters by trace id rather than relying
/// on the bag being empty.
public sealed class InMemoryTelemetryFixture : IDisposable
{
    private readonly TracerProvider _tracerProvider;

    public InMemoryTelemetryFixture()
    {
        Captured = new List<Activity>();

        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetSampler(new AlwaysOnSampler())
            .AddSource(PaymentsActivitySource.Name)
            .AddSource("Microsoft.AspNetCore")
            .AddSource("MassTransit")
            .AddSource("Npgsql")
            .AddInMemoryExporter(Captured)
            .Build()!;
    }

    public List<Activity> Captured { get; }

    public IReadOnlyList<Activity> SnapshotForTrace(string traceId) =>
        Captured
            .Where(a => string.Equals(a.TraceId.ToString(), traceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public void Dispose() => _tracerProvider.Dispose();
}
