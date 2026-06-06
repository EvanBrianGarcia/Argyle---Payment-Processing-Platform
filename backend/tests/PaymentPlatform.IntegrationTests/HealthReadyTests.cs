using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

/// Phase 3 Task 8. Exercises `/health/ready` against the real Postgres +
/// RabbitMQ fixtures so a stopped broker actually trips the readiness
/// probe — Phase 1's `/health/live` only proved the process was up.
[Collection(MessagingTestCollection.Name)]
public sealed class HealthReadyTests : IAsyncLifetime
{
    private readonly MessagingFixture _fixture;
    private MessagingApiFactory _factory = default!;
    private HttpClient _client = default!;
    private static int _migrationCheck;

    public HealthReadyTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await _fixture.Postgres.ResetDatabaseAsync();

        if (Interlocked.CompareExchange(ref _migrationCheck, 1, 0) == 0)
        {
            await EnsureMigratedAsync();
        }

        _factory = new MessagingApiFactory(_fixture);
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetReady_Returns200_WhenPostgresAndRabbitMqAreUp()
    {
        var response = await _client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("status").GetString().Should().Be("healthy");

        var checks = json.RootElement.GetProperty("checks");
        checks.GetArrayLength().Should().Be(2);

        foreach (var check in checks.EnumerateArray())
        {
            check.GetProperty("healthy").GetBoolean().Should().BeTrue(
                because: $"{check.GetProperty("name").GetString()} should be reachable when its fixture is running");
        }
    }

    [Fact]
    public async Task GetReady_Returns503_WhenRabbitMqIsUnreachable()
    {
        // Point the running host at a port nothing listens on — simulates an
        // outage without tearing down the broker container the rest of the
        // collection depends on.
        using var brokenFactory = new MessagingApiFactory(_fixture, rabbitMqPortOverride: 1);
        using var brokenClient = brokenFactory.CreateClient();

        var response = await brokenClient.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("status").GetString().Should().Be("unhealthy");

        var rabbit = json.RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "rabbitmq");
        rabbit.GetProperty("healthy").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetReady_Returns200_AfterRabbitMqIsRestored()
    {
        // Sanity: the shared broker is still up, and a fresh request through
        // the original client returns healthy. Guards against the broken
        // factory's env-var blip leaking into subsequent runs.
        var response = await _client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task EnsureMigratedAsync()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_fixture.Postgres.ConnectionString)
            .Options;
        await using var db = new PaymentsDbContext(options);
        await db.Database.MigrateAsync();
    }
}
