using System.Collections.Concurrent;
using System.Diagnostics;
using MassTransit;
using PaymentPlatform.Infrastructure.Diagnostics;

namespace PaymentPlatform.Infrastructure.Messaging;

/// MassTransit consume observer that translates pipeline events into the
/// queue-side success and dead-letter metrics on PaymentsMeter. Wired via
/// `cfg.AddConsumeObserver<MetricsConsumerObserver>()` from the Worker's
/// MassTransit registration so it fires for every consumer on every endpoint.
///
/// Counting model:
///  - PostConsume → mq_consumed_total +1 plus a duration observation.
///  - ConsumeFault → mq_deadletter_total +1 — this delivery's outer
///    ConsumeContext is exiting the pipe with an unhandled exception, which
///    means the receive endpoint will route it to the `_error` queue.
///
/// Retries are counted in SettlePaymentConsumer itself, not here, because
/// the consume-observer chain wraps the retry filter — ConsumeFault fires
/// once per delivery rather than per attempt, and the outer ConsumeContext
/// has lost its RetryContext payload by then. The inner attempts each get
/// their own ConsumeContext with a live retry attempt that the consumer can
/// read.
///
/// Duration uses Stopwatch.GetTimestamp / GetElapsedTime per the dotnet
/// OTel hot-path guidance (no Stopwatch allocation per message). The
/// start-time map is keyed by ConsumeContext to handle concurrent dispatch.
public sealed class MetricsConsumerObserver : IConsumeObserver
{
    private readonly PaymentsMeter _meter;
    private readonly ConcurrentDictionary<object, long> _starts = new();

    public MetricsConsumerObserver(PaymentsMeter meter)
    {
        _meter = meter;
    }

    public Task PreConsume<T>(ConsumeContext<T> context) where T : class
    {
        _starts[context] = Stopwatch.GetTimestamp();
        return Task.CompletedTask;
    }

    public Task PostConsume<T>(ConsumeContext<T> context) where T : class
    {
        var queue = ExtractQueueName(context);
        _meter.RecordConsumed(queue, MeasureDuration(context));
        return Task.CompletedTask;
    }

    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
    {
        var queue = ExtractQueueName(context);
        _meter.RecordDeadLetter(queue);

        // Drop the start timestamp without recording a duration observation —
        // faulted duration buckets would skew the success-path histogram.
        _starts.TryRemove(context, out _);
        return Task.CompletedTask;
    }

    private TimeSpan MeasureDuration<T>(ConsumeContext<T> context) where T : class
    {
        if (_starts.TryRemove(context, out var start))
        {
            return Stopwatch.GetElapsedTime(start);
        }

        // PreConsume was never seen — e.g., the observer attached after
        // the message had already started. Avoid a negative-duration
        // observation that would corrupt the histogram.
        return TimeSpan.Zero;
    }

    private static string ExtractQueueName<T>(ConsumeContext<T> context) where T : class
    {
        // ReceiveContext.InputAddress is the rabbit URI of the receive endpoint,
        // e.g., rabbitmq://host/settlement. The last path segment is the queue.
        var input = context.ReceiveContext.InputAddress;
        var segments = input.AbsolutePath.Trim('/').Split('/');
        return segments.Length == 0 ? "unknown" : segments[^1];
    }
}
