namespace PaymentPlatform.Worker.Consumers;

/// Tunable retry policy for the SettlePaymentConsumer's receive endpoint.
///
/// Defaults match master plan §7's 1s → 4s → 16s → 64s → 256s exponential
/// schedule. Integration tests override every interval to single-digit
/// milliseconds so the retry/DLQ suite finishes in seconds.
public sealed class WorkerRetryOptions
{
    public const string SectionName = "Worker:Retry";

    public int RetryLimit { get; init; } = 5;

    public int BaseIntervalMs { get; init; } = 1_000;

    public int MaxIntervalMs { get; init; } = 256_000;

    public int IncrementMs { get; init; } = 4_000;
}
