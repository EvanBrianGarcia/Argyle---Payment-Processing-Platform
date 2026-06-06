namespace PaymentPlatform.IntegrationTests.Fixtures;

/// xUnit class fixture that owns both Postgres and RabbitMQ containers for
/// tests that exercise the queue boundary. Containers are class-scoped so
/// the cold-start cost (~30s) hits once per test class.
public sealed class MessagingFixture : IAsyncLifetime
{
    public PostgresFixture Postgres { get; } = new();

    public RabbitMqFixture RabbitMq { get; } = new();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Postgres.InitializeAsync(), RabbitMq.InitializeAsync());
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(Postgres.DisposeAsync(), RabbitMq.DisposeAsync());
    }
}
