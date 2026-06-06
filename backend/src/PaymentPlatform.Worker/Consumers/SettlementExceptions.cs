namespace PaymentPlatform.Worker.Consumers;

/// Raised by SettlePaymentConsumer when the processor reports a retryable
/// failure (network blip, upstream 5xx, timeout). MassTransit retries
/// these by default — Task 7 wires the exponential backoff policy.
public sealed class TransientSettlementException : Exception
{
    public TransientSettlementException(string reason)
        : base($"Transient settlement failure: {reason}")
    {
    }
}

/// Raised by SettlePaymentConsumer when the processor reports a failure
/// that retrying cannot fix (card declined, validation rejection, unknown
/// payment id). Task 7's retry policy adds this to the Ignore<> list so
/// the message routes straight to the DLQ instead of looping.
public sealed class PermanentSettlementFailureException : Exception
{
    public PermanentSettlementFailureException(string reason)
        : base($"Permanent settlement failure: {reason}")
    {
    }
}
