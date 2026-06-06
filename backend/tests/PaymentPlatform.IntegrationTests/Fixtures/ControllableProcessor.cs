using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.IntegrationTests.Fixtures;

/// Test-only IPaymentProcessor whose per-call result is fully controllable
/// from the test method. StubPaymentProcessor's IOptions snapshot binds at
/// host build time, which prevents per-test reconfiguration; this double
/// removes that constraint so each test owns the outcome explicitly.
internal sealed class ControllableProcessor : IPaymentProcessor
{
    private int _callCount;

    public Func<SettlePayment, ProcessorResult> ResultFor { get; set; }
        = _ => new ProcessorResult.Success("stub_ref_default");

    public int CallCount => Volatile.Read(ref _callCount);

    public Task<ProcessorResult> SettleAsync(SettlePayment message, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(ResultFor(message));
    }
}
