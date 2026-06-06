using Testcontainers.RabbitMq;

namespace PaymentPlatform.IntegrationTests.Fixtures;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private const string DefaultUser = "guest";
    private const string DefaultPassword = "guest";

    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:3-management-alpine")
        .WithUsername(DefaultUser)
        .WithPassword(DefaultPassword)
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(5672);

    public string Username => DefaultUser;

    public string Password => DefaultPassword;

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
