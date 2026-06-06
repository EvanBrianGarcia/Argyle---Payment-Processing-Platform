namespace PaymentPlatform.Api.Configuration;

public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "Outbox:Dispatcher";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; init; } = 16;
}
