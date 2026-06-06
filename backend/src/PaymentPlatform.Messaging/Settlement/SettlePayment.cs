namespace PaymentPlatform.Messaging.Settlement;

public sealed record SettlePayment(
    string MessageId,
    string PaymentId,
    string MerchantId,
    long AmountMinor,
    string Currency,
    string CorrelationId,
    int Attempt,
    DateTimeOffset EnqueuedAt);
