using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentPlatform.Api.Configuration;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Common;
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
            using var __ = LogContext.PushProperty("trace_id", row.CorrelationId);

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
}
