using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;
using PaymentPlatform.Worker.Consumers;

namespace PaymentPlatform.IntegrationTests.Diagnostics;

/// Phase 4 Task 7 — capstone acceptance test. Codifies the §2 walkthrough from
/// `.claude/plans/payment-platform-phase-4.plan.md` against the live API +
/// Worker so a regression in Tasks 1–6 surfaces here as a single failing test
/// instead of requiring a full manual `docker compose up` walk.
///
/// Asserts the four observability claims the brief makes about Phase 4:
///   1. Trace propagation — one W3C trace covers POST /v1/payments → capture
///      → outbox publish → MassTransit consume.
///   2. RED + business metrics on API /metrics.
///   3. Queue metrics on Worker /metrics (dedicated port).
///   4. trace_id appears on both API and Worker log lines for the same
///      payment.
[Collection(MessagingTestCollection.Name)]
public sealed class ObservabilityAcceptanceTests
    : IClassFixture<InMemoryTelemetryFixture>, IAsyncLifetime
{
    private const string AcmeKey = "dev-key-mrc-acme";

    private readonly MessagingFixture _fixture;
    private readonly InMemoryTelemetryFixture _telemetry;
    private MessagingApiFactory _apiFactory = default!;
    private HttpClient _client = default!;
    private SettlementWorkerHost _worker = default!;
    private static int _migrationCheck;

    public ObservabilityAcceptanceTests(
        MessagingFixture fixture,
        InMemoryTelemetryFixture telemetry)
    {
        _fixture = fixture;
        _telemetry = telemetry;
    }

    public async Task InitializeAsync()
    {
        await _fixture.Postgres.ResetDatabaseAsync();

        if (Interlocked.CompareExchange(ref _migrationCheck, 1, 0) == 0)
        {
            await EnsureMigratedAsync();
        }

        // Match the cadence MetricsEndpointTests uses — fast enough that the
        // gauge updater publishes within the test deadline and the dispatcher
        // empties the outbox without waiting for the production 2s default.
        Environment.SetEnvironmentVariable("Diagnostics__StatusGaugeInterval", "00:00:00.200");
        Environment.SetEnvironmentVariable("Outbox__Dispatcher__PollInterval", "00:00:00.500");

        _apiFactory = new MessagingApiFactory(_fixture);
        _client = _apiFactory.CreateClient();

        var fastRetry = new WorkerRetryOptions
        {
            RetryLimit = 5,
            BaseIntervalMs = 50,
            MaxIntervalMs = 200,
            IncrementMs = 50,
        };
        _worker = await SettlementWorkerHost.StartAsync(_fixture, fastRetry);
        _worker.Clock.UtcNow = DateTimeOffset.UtcNow.AddMinutes(1);
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("Diagnostics__StatusGaugeInterval", null);
        Environment.SetEnvironmentVariable("Outbox__Dispatcher__PollInterval", null);
        await _worker.DisposeAsync();
        _client.Dispose();
        _apiFactory.Dispose();
    }

    [Fact]
    public async Task FullObservabilityWalkthrough_TracesMetricsAndLogs_AreWiredEndToEnd()
    {
        _apiFactory.LogSink.Clear();
        _worker.LogSink.Clear();
        var beforeSpans = _telemetry.Captured.Count;

        // 1. Create + capture a payment — drives the full state machine
        //    through the API and the worker so every observability surface
        //    has data to expose.
        var paymentId = await CreatePaymentAsync();
        await AuthorizeInProcessAsync(paymentId);
        var captureResponse = await CaptureAsync(paymentId);

        captureResponse.Headers.TryGetValues("traceparent", out var traceparentValues)
            .Should().BeTrue("every API response must carry a W3C traceparent header (Task 5)");
        var captureTraceparent = traceparentValues!.Single();
        captureTraceparent.Should().MatchRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$");
        var captureTraceId = ExtractTraceId(captureTraceparent);

        // 2. Wait for the worker to land on Settled.
        await Eventually(async () =>
        {
            var status = await ReadStatusAsync(paymentId);
            status.Should().Be("Settled",
                "the worker must complete the Captured → Settled transition end-to-end");
        }, TimeSpan.FromSeconds(20));

        // ─── Trace claim ───
        await Eventually(() =>
        {
            var captureSpans = _telemetry.Captured.Skip(beforeSpans)
                .Where(a => string.Equals(a.TraceId.ToString(), captureTraceId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            captureSpans.Should().Contain(
                a => a.Kind == ActivityKind.Server,
                "the API capture call must emit a server span in the response's trace");
            captureSpans.Should().Contain(
                a => a.Kind == ActivityKind.Consumer,
                "the worker's settlement consume must land in the SAME trace as the API capture — proving cross-process traceparent propagation");
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(10));

        // ─── API /metrics claims ───
        var apiMetrics = await ScrapeMetricsAsync(_client);
        apiMetrics.Should().MatchRegex(
            "payments_created_total\\{currency=\"USD\",merchant_id=\"mrc_acme\"\\} [1-9]\\d*",
            "the business counter must increment on POST /v1/payments");
        apiMetrics.Should().Contain("http_requests_received_total",
            "the prometheus-net RED middleware must expose HTTP request counts");

        await Eventually(async () =>
        {
            var body = await ScrapeMetricsAsync(_client);
            body.Should().MatchRegex(
                "payments_by_status\\{status=\"Settled\"\\} [1-9]\\d*",
                "the PaymentStatusGaugeUpdater must publish a non-zero Settled gauge after the worker lands the transition");
        }, TimeSpan.FromSeconds(8));

        // ─── Worker /metrics claims (dedicated port) ───
        using var workerMetricsClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{_worker.MetricsPort}"),
        };
        var workerMetrics = await ScrapeMetricsAsync(workerMetricsClient);
        workerMetrics.Should().MatchRegex(
            "mq_consumed_total\\{queue=\"settlement\"\\} [1-9]\\d*",
            "the worker's own /metrics surface must expose the consumed counter — that's the point of Task 4");
        workerMetrics.Should().Contain(
            "mq_processing_duration_seconds_count{queue=\"settlement\"}",
            "the MetricsConsumerObserver must observe the per-message duration on the worker");

        // ─── Log claim ───
        await Eventually(() =>
        {
            var apiLines = _apiFactory.LogSink.Lines
                .Where(line => line.Contains($"\"trace_id\":\"{captureTraceId}\""))
                .ToArray();
            apiLines.Should().NotBeEmpty(
                "at least one API log line must carry the capture's W3C trace_id (TraceIdEnricher invariant)");

            var workerLines = _worker.LogSink.Lines
                .Where(line => line.Contains($"\"trace_id\":\"{captureTraceId}\""))
                .ToArray();
            workerLines.Should().NotBeEmpty(
                "at least one worker consumer log line must carry the SAME trace_id — proving log + trace correlation across the AMQP boundary");

            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(15));
    }

    private static string ExtractTraceId(string traceparent)
    {
        // W3C traceparent format: version-traceId-spanId-flags
        var parts = traceparent.Split('-');
        parts.Should().HaveCount(4, $"traceparent must have four hyphen-separated parts; got '{traceparent}'");
        return parts[1];
    }

    private static async Task<string> ScrapeMetricsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/metrics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string> CreatePaymentAsync()
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

        using var json = await TestJson.ParseAsync(response);
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private async Task AuthorizeInProcessAsync(string paymentId)
    {
        await using var scope = _apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId);
        var evt = payment.Authorize(clock.UtcNow);
        db.PaymentEvents.Add(evt);
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> CaptureAsync(string paymentId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/payments/{paymentId}/capture")
        {
            Content = TestJson.Content(new { }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return response;
    }

    private async Task<string> ReadStatusAsync(string paymentId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/payments/{paymentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = await TestJson.ParseAsync(response);
        return json.RootElement.GetProperty("status").GetString() ?? string.Empty;
    }

    private async Task EnsureMigratedAsync()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_fixture.Postgres.ConnectionString)
            .Options;
        await using var db = new PaymentsDbContext(options);
        await db.Database.MigrateAsync();
    }

    private static async Task Eventually(Func<Task> assertion, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastFailure = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await assertion();
                return;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                await Task.Delay(200);
            }
        }
        throw lastFailure ?? new Xunit.Sdk.XunitException("Eventually() timed out without assertion executing.");
    }
}
