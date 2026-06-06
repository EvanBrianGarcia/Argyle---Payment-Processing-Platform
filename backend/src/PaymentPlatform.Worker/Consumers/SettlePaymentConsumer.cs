using MassTransit;
using Microsoft.Extensions.Logging;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.Worker.Consumers;

/// Phase 3 Task 3 — wiring skeleton. The consumer logs receipt and acks.
/// Task 6 replaces this with the full idempotent FOR-UPDATE settlement
/// behavior driven by IPaymentProcessor.
public sealed class SettlePaymentConsumer : IConsumer<SettlePayment>
{
    private readonly ILogger<SettlePaymentConsumer> _logger;

    public SettlePaymentConsumer(ILogger<SettlePaymentConsumer> logger)
    {
        _logger = logger;
    }

    public Task Consume(ConsumeContext<SettlePayment> context)
    {
        _logger.LogInformation(
            "SettlePayment received for {PaymentId} (correlation_id={CorrelationId})",
            context.Message.PaymentId,
            context.Message.CorrelationId);
        return Task.CompletedTask;
    }
}
