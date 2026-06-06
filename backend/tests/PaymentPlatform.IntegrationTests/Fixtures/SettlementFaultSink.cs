using System.Collections.Concurrent;
using MassTransit;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.IntegrationTests.Fixtures;

/// Records every SettlePayment that the consume pipeline reports as faulted.
/// Wired through SettlementConsumeFaultObserver — observer hooks fire
/// whenever IConsumer.Consume throws, independent of queue topology, so
/// the DLQ test does not depend on RabbitMQ's internal error-queue routing.
public sealed class SettlementFaultSink
{
    private readonly ConcurrentQueue<(SettlePayment Message, Exception Exception)> _faults = new();
    private readonly SemaphoreSlim _signal = new(0);

    public void Record(SettlePayment message, Exception exception)
    {
        _faults.Enqueue((message, exception));
        _signal.Release();
    }

    public IReadOnlyCollection<(SettlePayment Message, Exception Exception)> Received => _faults.ToArray();

    public async Task<(SettlePayment Message, Exception Exception)?> WaitForNextAsync(TimeSpan timeout)
    {
        if (await _signal.WaitAsync(timeout))
        {
            _faults.TryPeek(out var first);
            return first;
        }
        return null;
    }
}

/// MassTransit observer that records consume faults into a SettlementFaultSink.
/// Fires after retry policy decides not to redeliver, so this catches both
/// retry-exhaustion and Ignore<> matches.
public sealed class SettlementConsumeFaultObserver : IConsumeObserver
{
    private readonly SettlementFaultSink _sink;

    public SettlementConsumeFaultObserver(SettlementFaultSink sink)
    {
        _sink = sink;
    }

    public Task PreConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;

    public Task PostConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;

    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
    {
        if (context.Message is SettlePayment settle)
        {
            _sink.Record(settle, exception);
        }
        return Task.CompletedTask;
    }
}
