using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentPlatform.Api.Configuration;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Application.Diagnostics;
using PaymentPlatform.Infrastructure.Persistence;
using Serilog.Context;

namespace PaymentPlatform.Api.HostedServices;

/// Polls payment_outbox for undispatched rows on PollInterval, publishes each
/// via IOutboxPublisher, then flips dispatched_at via ExecuteUpdateAsync.
/// Single-host for Phase 3 per ADR-0008; multi-host needs SKIP LOCKED.
///
/// Publish-then-flip ordering is intentional: a crash between the two causes
/// a duplicate publish on the next poll, which the idempotent consumer
/// absorbs (ADR-0010). The reverse ordering would lose messages.
public sealed class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly OutboxDispatcherOptions _options;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        ILogger<OutboxDispatcher> logger,
        IOptions<OutboxDispatcherOptions> options)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int dispatched;
            try
            {
                dispatched = await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox dispatch iteration failed; will retry after poll interval.");
                dispatched = 0;
            }

            if (dispatched == 0)
            {
                try
                {
                    await Task.Delay(_options.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IOutboxPublisher>();

        var batch = await db.PaymentOutbox
            .AsNoTracking()
            .Where(o => o.DispatchedAt == null)
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var row in batch)
        {
            using var _ = LogContext.PushProperty("payment_id", row.AggregateId);
            // OTel Activity (via TraceIdEnricher) owns trace_id/span_id; this
            // surfaces the publish-time ULID under its own key so dispatcher
            // and consumer logs can be joined back to the originating row.
            using var __ = LogContext.PushProperty("correlation_id", row.CorrelationId);

            // Restore the originating capture's W3C trace context so the
            // MassTransit publish span — and the worker's downstream consume
            // span — land inside that trace instead of the dispatcher's own
            // background root trace. When the row predates Task 5 (no
            // persisted traceparent) we skip the explicit publish activity
            // entirely; MassTransit then starts its own root publish span —
            // matching pre-Task-5 behavior — instead of accidentally
            // parenting to whatever ambient Activity.Current may carry.
            var parentContext = TryParseTraceparent(row.Traceparent);
            using var publishActivity = parentContext is ActivityContext ctx
                ? PaymentsActivitySource.Source.StartActivity(
                    "OutboxDispatcher.Publish",
                    ActivityKind.Producer,
                    ctx)
                : null;
            publishActivity?.SetTag("payment_id", row.AggregateId);

            var message = OutboxMessageFactory.DeserializeSettlement(row);
            await publisher.PublishSettlementAsync(message, cancellationToken);

            var dispatchedAt = _clock.UtcNow;
            await db.PaymentOutbox
                .Where(o => o.Id == row.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(o => o.DispatchedAt, dispatchedAt),
                    cancellationToken);

            _logger.LogInformation(
                "Dispatched outbox row {OutboxId} for payment {PaymentId}.",
                row.Id,
                row.AggregateId);
        }

        return batch.Count;
    }

    private static ActivityContext? TryParseTraceparent(string? traceparent)
    {
        if (string.IsNullOrWhiteSpace(traceparent))
        {
            return null;
        }
        // ActivityContext.TryParse handles the W3C "00-<traceid>-<spanid>-<flags>"
        // form. Returning null on malformed input keeps a stray row from
        // crashing the dispatcher loop.
        return ActivityContext.TryParse(traceparent, traceState: null, out var ctx) ? ctx : null;
    }
}
