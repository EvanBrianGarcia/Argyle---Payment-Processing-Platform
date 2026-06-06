using System.Diagnostics;
using System.Text.Json;
using NUlid;
using PaymentPlatform.Domain.Outbox;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.Application.Common;

public static class OutboxMessageFactory
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false,
    };

    public static PaymentOutboxMessage ForSettlement(
        Payment payment,
        string correlationId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("Correlation id must be non-blank.", nameof(correlationId));
        }

        var message = new SettlePayment(
            MessageId: "msg_" + Ulid.NewUlid().ToString(),
            PaymentId: payment.Id,
            MerchantId: payment.MerchantId,
            AmountMinor: payment.Amount.AmountMinor,
            Currency: payment.Amount.Currency,
            CorrelationId: correlationId,
            Attempt: 1,
            EnqueuedAt: now);

        var payload = JsonSerializer.Serialize(message, PayloadJsonOptions);

        // Capture the active W3C trace context so OutboxDispatcher can restore
        // it before publish. Without this, the dispatcher's background loop
        // publishes under its own root trace, severing the API → Worker chain.
        var traceparent = Activity.Current?.Id;

        return PaymentOutboxMessage.Create(
            aggregateId: payment.Id,
            messageType: OutboxMessageType.Settlement,
            payload: payload,
            correlationId: correlationId,
            createdAt: now,
            traceparent: traceparent);
    }

    public static SettlePayment DeserializeSettlement(PaymentOutboxMessage outboxMessage)
    {
        ArgumentNullException.ThrowIfNull(outboxMessage);

        return JsonSerializer.Deserialize<SettlePayment>(outboxMessage.Payload, PayloadJsonOptions)
            ?? throw new InvalidOperationException(
                $"Outbox message {outboxMessage.Id} payload could not be deserialized as SettlePayment.");
    }
}
