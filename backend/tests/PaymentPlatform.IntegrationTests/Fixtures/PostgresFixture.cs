using Testcontainers.PostgreSql;

namespace PaymentPlatform.IntegrationTests.Fixtures;

public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ResetSql =
        "TRUNCATE TABLE payment_outbox, payment_events, payments, idempotency_keys RESTART IDENTITY CASCADE;";

    private const string DbUser = "postgres";
    private const string DbPassword = "postgres";
    private const string DbName = "payments";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithUsername(DbUser)
        .WithPassword(DbPassword)
        .WithDatabase(DbName)
        .WithEnvironment("POSTGRES_USER", DbUser)
        .WithEnvironment("POSTGRES_PASSWORD", DbPassword)
        .WithEnvironment("POSTGRES_DB", DbName)
        .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task ResetDatabaseAsync()
    {
        var result = await _container.ExecScriptAsync(ResetSql);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to reset database. ExitCode={result.ExitCode} Stderr={result.Stderr}");
        }
    }
}
