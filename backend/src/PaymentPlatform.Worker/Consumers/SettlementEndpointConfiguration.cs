using MassTransit;

namespace PaymentPlatform.Worker.Consumers;

/// Centralizes the retry + DLQ + skipped-message topology for the settlement
/// receive endpoint so the production Worker host and integration tests share
/// the exact same MassTransit pipeline shape — divergence between the two
/// would let retry/DLQ wiring bugs slip past the test suite.
public static class SettlementEndpointConfiguration
{
    public static void ConfigureSettlementEndpoint(
        this IReceiveEndpointConfigurator endpoint,
        IRegistrationContext context,
        WorkerRetryOptions retry)
    {
        endpoint.UseMessageRetry(r =>
        {
            r.Exponential(
                retryLimit: retry.RetryLimit,
                minInterval: TimeSpan.FromMilliseconds(retry.BaseIntervalMs),
                maxInterval: TimeSpan.FromMilliseconds(retry.MaxIntervalMs),
                intervalDelta: TimeSpan.FromMilliseconds(retry.IncrementMs));

            // Permanent failures route straight to the error queue without
            // burning retry budget — the consumer signals this contract by
            // throwing PermanentSettlementFailureException.
            r.Ignore<PermanentSettlementFailureException>();
        });

        // Skipped messages (no consumer matched the message type on this
        // endpoint) are dropped rather than forwarded to a skipped queue.
        endpoint.DiscardSkippedMessages();

        endpoint.ConfigureConsumer<SettlePaymentConsumer>(context);
    }
}
