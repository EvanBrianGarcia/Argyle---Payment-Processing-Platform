namespace PaymentPlatform.IntegrationTests.Fixtures;

public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected PostgresFixture Postgres { get; }
    protected PaymentApiFactory Factory { get; }
    protected HttpClient Client { get; }

    protected IntegrationTestBase(PostgresFixture postgres)
    {
        Postgres = postgres;
        Factory = new PaymentApiFactory(postgres.ConnectionString);
        Client = Factory.CreateClient();
    }

    public virtual async Task InitializeAsync()
    {
        await Postgres.ResetDatabaseAsync();
        Factory.LogSink.Clear();
    }

    public virtual Task DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}
