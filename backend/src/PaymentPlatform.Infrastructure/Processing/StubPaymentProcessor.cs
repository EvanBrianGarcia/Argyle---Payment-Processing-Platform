using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.Infrastructure.Processing;

public sealed class StubPaymentProcessor : IPaymentProcessor
{
    private readonly StubProcessorOptions _options;
    private readonly ConcurrentDictionary<string, int> _callCounts = new();

    public StubPaymentProcessor(IOptions<StubProcessorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<ProcessorResult> SettleAsync(SettlePayment message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var callCount = _callCounts.AddOrUpdate(message.PaymentId, 1, (_, current) => current + 1);
        var mode = ResolveMode(message.PaymentId);

        ProcessorResult result = mode switch
        {
            StubProcessorMode.AlwaysSucceed =>
                new ProcessorResult.Success(BuildExternalReference(message)),
            StubProcessorMode.FailNTimesThenSucceed when callCount <= _options.FailureCount =>
                new ProcessorResult.TransientFailure("transient_stub"),
            StubProcessorMode.FailNTimesThenSucceed =>
                new ProcessorResult.Success(BuildExternalReference(message)),
            StubProcessorMode.AlwaysFailPermanent =>
                new ProcessorResult.PermanentFailure("permanent_stub"),
            _ => throw new InvalidOperationException($"Unhandled StubProcessorMode '{mode}'."),
        };

        return Task.FromResult(result);
    }

    public int CallCountFor(string paymentId) =>
        _callCounts.TryGetValue(paymentId, out var count) ? count : 0;

    public void ResetCounts() => _callCounts.Clear();

    private StubProcessorMode ResolveMode(string paymentId) =>
        _options.PerPaymentOverrides is { Count: > 0 } overrides
            && overrides.TryGetValue(paymentId, out var overrideMode)
                ? overrideMode
                : _options.Mode;

    private static string BuildExternalReference(SettlePayment message) =>
        $"stub_ref_{message.PaymentId}";
}
