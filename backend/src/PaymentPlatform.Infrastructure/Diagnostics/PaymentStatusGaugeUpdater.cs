using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;

namespace PaymentPlatform.Infrastructure.Diagnostics;

/// Phase 4 Task 3 — periodic sampler that drives `payments_by_status` gauges.
///
/// Sampled rather than event-driven so we don't have to keep a gauge live
/// across every handler/consumer path that changes a payment's status —
/// `SELECT status, COUNT(*) GROUP BY status` once per interval is bounded and
/// cheap thanks to the Phase 1 index on `payments.status`.
///
/// Registered in both API and Worker; each process maintains its own
/// process-local gauge and Prometheus scrapes them separately. The interval
/// is configurable so the integration suite can drive a 200ms cadence
/// without the production 30s default holding up tests.
public sealed class PaymentStatusGaugeUpdater : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PaymentsMeter _meter;
    private readonly IOptionsMonitor<DiagnosticsOptions> _options;
    private readonly ILogger<PaymentStatusGaugeUpdater> _logger;

    public PaymentStatusGaugeUpdater(
        IServiceScopeFactory scopeFactory,
        PaymentsMeter meter,
        IOptionsMonitor<DiagnosticsOptions> options,
        ILogger<PaymentStatusGaugeUpdater> logger)
    {
        _scopeFactory = scopeFactory;
        _meter = meter;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The gauge updater is best-effort observability — one
                // failed sample shouldn't take the service down. Log and
                // try again on the next tick.
                _logger.LogWarning(ex, "PaymentStatusGaugeUpdater sample failed.");
            }

            try
            {
                await Task.Delay(_options.CurrentValue.StatusGaugeInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var grouped = await db.Payments
            .AsNoTracking()
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = (long)g.Count() })
            .ToListAsync(cancellationToken);

        // Initialise every status to zero so a state that drained since the
        // last sample falls back to zero in the gauge rather than getting
        // stuck at its last non-zero reading.
        var counts = new Dictionary<PaymentStatus, long>(capacity: 6);
        foreach (var status in Enum.GetValues<PaymentStatus>())
        {
            counts[status] = 0L;
        }
        foreach (var row in grouped)
        {
            counts[row.Status] = row.Count;
        }

        _meter.SetPaymentStatusCounts(counts);
    }
}
