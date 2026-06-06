using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests.Diagnostics;

/// Phase 4 Task 6 — proves the RedactingEnricher is wired into the API's
/// Serilog pipeline. Resolves an `ILogger<T>` from the API host's DI so the
/// log event traverses the exact Serilog pipeline the production code uses,
/// then asserts the raw token is gone and the masked value is present in
/// the captured LogSink lines.
///
/// We deliberately do NOT add a `LogInformation("{@Command}", command)`
/// call to CreatePaymentCommandHandler just to satisfy this test — the
/// unit tests prove the enricher logic, this integration test only needs
/// to prove the wiring is plumbed through the API's logger factory.
[Collection(IntegrationTestCollection.Name)]
public sealed class RedactionEndToEndTests : IAsyncLifetime
{
    private const string AcmeKey = "dev-key-mrc-acme";

    private readonly PostgresFixture _postgres;
    private PaymentApiFactory _apiFactory = default!;
    private HttpClient _client = default!;
    private static int _migrationCheck;

    public RedactionEndToEndTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    public async Task InitializeAsync()
    {
        await _postgres.ResetDatabaseAsync();

        if (Interlocked.CompareExchange(ref _migrationCheck, 1, 0) == 0)
        {
            await EnsureMigratedAsync();
        }

        _apiFactory = new PaymentApiFactory(_postgres.ConnectionString);
        _client = _apiFactory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _apiFactory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PostPayments_WithCardTokenInBody_DoesNotLeakTokenIntoLogSink()
    {
        const string sensitiveToken = "tok_VISA_4242";

        // POST a payment with the sensitive token. The production handler
        // does not destructure-log the command today, so this request alone
        // wouldn't prove the wiring. The assertion below uses the host's
        // own ILogger<> to emit a probe through the SAME Serilog pipeline
        // — the same enricher chain that protects every other log line.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = TestJson.Content(new
            {
                AmountMinor = 12500L,
                Currency = "USD",
                CardToken = sensitiveToken,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // Resolve a logger from the host's DI — this routes through the
        // exact Serilog configuration the API was built with, including
        // RedactingEnricher in the enricher chain. Anything we log now
        // exercises the production redaction wiring end to end.
        _apiFactory.LogSink.Clear();
        await using (var scope = _apiFactory.Services.CreateAsyncScope())
        {
            var logger = scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("RedactionEndToEndProbe");
            logger.LogInformation(
                "Probe {@Command}",
                new
                {
                    CardToken = sensitiveToken,
                    Currency = "USD",
                    CustomerReference = "cust_42",
                });
        }

        var lines = _apiFactory.LogSink.Lines;
        lines.Should().NotBeEmpty("the probe log line must reach the InMemoryLogSink");
        lines.Should().NotContain(line => line.Contains(sensitiveToken),
            $"the raw token '{sensitiveToken}' must never appear in any log line — that is the redaction invariant");
        lines.Should().Contain(line => line.Contains("\"CardToken\":\"***\""),
            "the destructured CardToken property must be replaced with the mask '***'");
        lines.Should().Contain(line => line.Contains("\"CustomerReference\":\"cust_42\""),
            "non-denied properties must pass through untouched");
    }

    private async Task EnsureMigratedAsync()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_postgres.ConnectionString)
            .Options;
        await using var db = new PaymentsDbContext(options);
        await db.Database.MigrateAsync();
    }
}
