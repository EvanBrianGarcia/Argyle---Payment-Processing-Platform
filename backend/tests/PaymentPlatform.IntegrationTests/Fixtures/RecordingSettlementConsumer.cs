using System.Collections.Concurrent;
using MassTransit;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.IntegrationTests.Fixtures;

/// Test-only consumer that records every SettlePayment it receives so the
/// test can assert delivery happened. Backed by a singleton sink so the
/// consumer instances stay stateless and DI-friendly.
public sealed class RecordingSettlementConsumer : IConsumer<SettlePayment>
{
    private readonly SettlementSink _sink;

    public RecordingSettlementConsumer(SettlementSink sink)
    {
        _sink = sink;
    }

    public Task Consume(ConsumeContext<SettlePayment> context)
    {
        _sink.Record(context.Message);
        return Task.CompletedTask;
    }
}

public sealed class SettlementSink
{
    private readonly ConcurrentQueue<SettlePayment> _received = new();
    private readonly SemaphoreSlim _signal = new(0);

    public void Record(SettlePayment message)
    {
        _received.Enqueue(message);
        _signal.Release();
    }

    public IReadOnlyCollection<SettlePayment> Received => _received.ToArray();

    public async Task<SettlePayment?> WaitForNextAsync(TimeSpan timeout)
    {
        if (await _signal.WaitAsync(timeout))
        {
            _received.TryPeek(out var first);
            return first;
        }
        return null;
    }
}
