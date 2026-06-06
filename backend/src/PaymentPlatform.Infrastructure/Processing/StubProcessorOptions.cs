namespace PaymentPlatform.Infrastructure.Processing;

public sealed class StubProcessorOptions
{
    public const string SectionName = "Worker:StubProcessor";

    public StubProcessorMode Mode { get; init; } = StubProcessorMode.AlwaysSucceed;

    public int FailureCount { get; init; } = 2;

    public IDictionary<string, StubProcessorMode> PerPaymentOverrides { get; init; }
        = new Dictionary<string, StubProcessorMode>();
}

public enum StubProcessorMode
{
    AlwaysSucceed = 0,
    FailNTimesThenSucceed = 1,
    AlwaysFailPermanent = 2,
}
