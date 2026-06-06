using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests.Diagnostics;

/// Phase 4 Task 2 — `/metrics` endpoint exposing prometheus exposition.
/// RED first: nothing maps `/metrics` yet and `DevBearerAuthMiddleware`
/// still rejects unauthenticated requests for any path outside `/health`.
[Collection(MessagingTestCollection.Name)]
public sealed class MetricsEndpointTests : IAsyncLifetime
{
    private const string AcmeKey = "dev-key-mrc-acme";

    private readonly MessagingFixture _fixture;
    private MessagingApiFactory _apiFactory = default!;
    private HttpClient _client = default!;
    private static int _migrationCheck;

    public MetricsEndpointTests(MessagingFixture fixture)
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

        _apiFactory = new MessagingApiFactory(_fixture);
        _client = _apiFactory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _apiFactory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetMetrics_Returns200_WithPrometheusExpositionFormat()
    {
        var response = await _client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain",
            "Prometheus exposition format is served as text/plain");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("# TYPE",
            "the prometheus exposition format requires a TYPE metadata line per metric");
    }

    [Fact]
    public async Task GetMetrics_DoesNotRequireAuth()
    {
        // No Authorization header — must still succeed because Prometheus
        // scrapers do not send bearer tokens. Mirrors the existing
        // `/health/*` auth carve-out.
        var response = await _client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetMetrics_AfterPaymentsCreated_ExposesHttpRequestCounter()
    {
        // Seed five 201 responses on POST /v1/payments.
        for (var i = 0; i < 5; i++)
        {
            await CreatePaymentAsync();
        }

        var response = await _client.GetAsync("/metrics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("http_requests_received_total",
            "the prometheus-net AspNetCore middleware exposes http_requests_received_total");
    }

    private async Task CreatePaymentAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = TestJson.Content(new
            {
                AmountMinor = 12500L,
                Currency = "USD",
                CardToken = "tok_visa_demo",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task EnsureMigratedAsync()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<
            PaymentPlatform.Infrastructure.Persistence.PaymentsDbContext>()
            .UseNpgsql(_fixture.Postgres.ConnectionString)
            .Options;
        await using var db = new PaymentPlatform.Infrastructure.Persistence.PaymentsDbContext(options);
        await db.Database.MigrateAsync();
    }
}
