using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Diagnostics;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.Messaging.Settlement;
using SerilogLogContext = Serilog.Context.LogContext;

namespace PaymentPlatform.Worker.Consumers;

/// Phase 3 Task 6 — idempotent settlement consumer.
///
/// Wraps each consume in a database transaction that takes a SELECT FOR
/// UPDATE row lock on the payment row, so two concurrent deliveries against
/// the same payment id serialize. Short-circuits on already-Settled
/// (idempotent re-delivery) and on non-Captured states (state conflict).
/// Maps ProcessorResult variants to ack / TransientSettlementException /
/// PermanentSettlementFailureException — Task 7 wires the retry policy and
/// DLQ binding that interprets these.
public sealed class SettlePaymentConsumer : IConsumer<SettlePayment>
{
    private readonly PaymentsDbContext _db;
    private readonly IPaymentProcessor _processor;
    private readonly IClock _clock;
    private readonly ILogger<SettlePaymentConsumer> _logger;

    public SettlePaymentConsumer(
        PaymentsDbContext db,
        IPaymentProcessor processor,
        IClock clock,
        ILogger<SettlePaymentConsumer> logger)
    {
        _db = db;
        _processor = processor;
        _clock = clock;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SettlePayment> context)
    {
        var message = context.Message;
        using var activity = PaymentsActivitySource.Source.StartActivity("Settlement.Consume");
        activity?.SetTag("payment_id", message.PaymentId);
        activity?.SetTag("merchant_id", message.MerchantId);
        using var paymentScope = SerilogLogContext.PushProperty("payment_id", message.PaymentId);
        using var traceScope = SerilogLogContext.PushProperty("trace_id", message.CorrelationId);

        await using var tx = await _db.Database.BeginTransactionAsync(context.CancellationToken);

        // Take the row lock via a plain SELECT FOR UPDATE, then load via LINQ.
        // FromSqlRaw against the Payments DbSet would have collapsed lock and
        // load into one step, but EF Core's outer projection on FromSqlRaw
        // ignores HasColumnName overrides on ComplexProperty fields (Money's
        // amount_minor / currency), emitting "Amount_AmountMinor" / "Amount_Currency"
        // and breaking the query. Two-step lock-then-load avoids that path
        // and still serializes concurrent consumers on the same payment.
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT id FROM payments WHERE id = {0} FOR UPDATE",
            new object[] { message.PaymentId },
            context.CancellationToken);

        var payment = await _db.Payments
            .AsTracking()
            .FirstOrDefaultAsync(p => p.Id == message.PaymentId, context.CancellationToken);

        if (payment is null)
        {
            throw new PermanentSettlementFailureException("payment_not_found");
        }

        if (payment.Status == PaymentStatus.Settled)
        {
            _logger.LogInformation("Settlement skipped — payment already settled.");
            await tx.CommitAsync(context.CancellationToken);
            return;
        }

        if (payment.Status != PaymentStatus.Captured)
        {
            _logger.LogWarning(
                "Settlement skipped — payment in unexpected state {Status}.",
                payment.Status);
            await tx.CommitAsync(context.CancellationToken);
            return;
        }

        var result = await _processor.SettleAsync(message, context.CancellationToken);

        switch (result)
        {
            case ProcessorResult.Success:
                var settledEvent = payment.Settle(_clock.UtcNow);
                _db.PaymentEvents.Add(settledEvent);
                await _db.SaveChangesAsync(context.CancellationToken);
                await tx.CommitAsync(context.CancellationToken);
                _logger.LogInformation("Settlement committed.");
                return;

            case ProcessorResult.TransientFailure transient:
                throw new TransientSettlementException(transient.Reason);

            case ProcessorResult.PermanentFailure permanent:
                throw new PermanentSettlementFailureException(permanent.Reason);

            default:
                throw new InvalidOperationException(
                    $"Unhandled ProcessorResult type '{result.GetType().Name}'.");
        }
    }
}
