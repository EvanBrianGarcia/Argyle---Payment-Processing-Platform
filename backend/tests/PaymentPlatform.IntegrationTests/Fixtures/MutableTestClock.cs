using PaymentPlatform.Application.Abstractions;

namespace PaymentPlatform.IntegrationTests.Fixtures;

/// Test clock the consumer tests advance to assert that PaymentEvent.At
/// matches the clock value at the moment of settlement.
internal sealed class MutableTestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
}
