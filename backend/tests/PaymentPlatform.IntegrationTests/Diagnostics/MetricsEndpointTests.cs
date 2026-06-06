using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;
using PaymentPlatform.Messaging.Settlement;
using PaymentPlatform.Worker.Consumers;

namespace PaymentPlatform.IntegrationTests.Diagnostics;

/// Phase 4 Task 2 — `/metrics` endpoint exposing prometheus exposition.
/// Phase 4 Task 3 — business + queue metrics (payments_created_total,
/// refunds_total, mq_consumed_total, mq_retries_total, mq_deadletter_total,
/// payments_by_status).
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

        // Short interval lets the gauge-updater test poll without long sleeps.
        Environment.SetEnvironmentVariable("Diagnostics__StatusGaugeInterval", "00:00:00.200");
        // Outbox dispatcher polls fast so the settlement-driving tests are not
        // gated on the production 2s default cadence.
        Environment.SetEnvironmentVariable("Outbox__Dispatcher__PollInterval", "00:00:00.500");

        _apiFactory = new MessagingApiFactory(_fixture);
        _client = _apiFactory.CreateClient();
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("Diagnostics__StatusGaugeInterval", null);
        Environment.SetEnvironmentVariable("Outbox__Dispatcher__PollInterval", null);
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

    [Fact]
    public async Task GetMetrics_AfterPaymentsCreated_ExposesBusinessCounter()
    {
        // Seed three creates so the business counter is unambiguously > 0
        // even if other tests in the same process bumped the gauge first.
        for (var i = 0; i < 3; i++)
        {
            await CreatePaymentAsync();
        }

        var response = await _client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().MatchRegex(
            "payments_created_total\\{currency=\"USD\",merchant_id=\"mrc_acme\"\\} [1-9]\\d*",
            "the CreatePaymentCommandHandler should call IPaymentsMeter.RecordPaymentCreated on each success");
    }

    [Fact]
    public async Task GetMetrics_AfterSettlement_ExposesConsumedCounter()
    {
        // Drive a payment through the full happy path so the worker observer
        // increments mq_consumed_total on PostConsume.
        var fastRetry = new WorkerRetryOptions
        {
            RetryLimit = 5,
            BaseIntervalMs = 50,
            MaxIntervalMs = 200,
            IncrementMs = 50,
        };
        await using var worker = await SettlementWorkerHost.StartAsync(_fixture, fastRetry);
        worker.Clock.UtcNow = DateTimeOffset.UtcNow.AddMinutes(1);

        var paymentId = await CreatePaymentAsync();
        await AuthorizeInProcessAsync(paymentId);
        await CaptureAsync(paymentId);

        await Eventually(async () =>
        {
            var status = await ReadStatusAsync(paymentId);
            status.Should().Be("Settled");
        }, TimeSpan.FromSeconds(20));

        var response = await _client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().MatchRegex(
            "mq_consumed_total\\{queue=\"settlement\"\\} [1-9]\\d*",
            "MetricsConsumerObserver.PostConsume must increment mq_consumed_total for the settlement queue");
        body.Should().Contain("mq_processing_duration_seconds_count{queue=\"settlement\"}",
            "the same observer must also emit a histogram observation for processing duration");
    }

    [Fact]
    public async Task GetMetrics_AfterTransientRetriesThenPermanentFault_ExposesRetryAndDeadLetterCounters()
    {
        var fastRetry = new WorkerRetryOptions
        {
            RetryLimit = 5,
            BaseIntervalMs = 50,
            MaxIntervalMs = 200,
            IncrementMs = 50,
        };
        await using var worker = await SettlementWorkerHost.StartAsync(_fixture, fastRetry);
        worker.Clock.UtcNow = DateTimeOffset.UtcNow.AddMinutes(1);

        // First three calls fail transiently (retried), fourth fails
        // permanently (routed to DLQ via Ignore<PermanentSettlementFailureException>).
        var callCount = 0;
        worker.Processor.ResultFor = _ =>
        {
            var thisCall = Interlocked.Increment(ref callCount);
            return thisCall <= 3
                ? new ProcessorResult.TransientFailure("simulated_transient")
                : new ProcessorResult.PermanentFailure("simulated_permanent");
        };

        var paymentId = await SeedCapturedPaymentAsync(worker);

        await using (var publishScope = worker.Services.CreateAsyncScope())
        {
            var bus = publishScope.ServiceProvider.GetRequiredService<IBus>();
            await bus.Publish(BuildSettleMessage(paymentId, "trace-retry-dlq", worker.Clock.UtcNow));
        }

        var fault = await worker.Faults.WaitForNextAsync(TimeSpan.FromSeconds(45));
        fault.Should().NotBeNull(
            "the permanent fault should surface through MassTransit's ConsumeFault pipeline once retries are exhausted");

        // Eventually because the metrics scrape and the observer's final
        // increment can race the fault-sink signal by a few milliseconds.
        await Eventually(async () =>
        {
            var response = await _client.GetAsync("/metrics");
            var body = await response.Content.ReadAsStringAsync();

            body.Should().MatchRegex(
                "mq_retries_total\\{queue=\"settlement\"\\} [3-9]\\d*",
                "three transient failures must surface as at least three retry events");
            body.Should().MatchRegex(
                "mq_deadletter_total\\{queue=\"settlement\"\\} [1-9]\\d*",
                "the permanent-failure tail must register a dead-letter event");
        }, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task GetMetrics_OnWorkerDedicatedPort_ReturnsPrometheusExposition()
    {
        var fastRetry = new WorkerRetryOptions
        {
            RetryLimit = 5,
            BaseIntervalMs = 50,
            MaxIntervalMs = 200,
            IncrementMs = 50,
        };
        await using var worker = await SettlementWorkerHost.StartAsync(_fixture, fastRetry);

        using var workerClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{worker.MetricsPort}") };
        var response = await workerClient.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain",
            "the worker exposes the prometheus text exposition format on its dedicated port");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("# TYPE",
            "the worker's /metrics body should look like prometheus output, not an empty 200");
    }

    [Fact]
    public async Task GetMetrics_OnWorkerDedicatedPort_AfterSettlement_ExposesConsumedCounter()
    {
        var fastRetry = new WorkerRetryOptions
        {
            RetryLimit = 5,
            BaseIntervalMs = 50,
            MaxIntervalMs = 200,
            IncrementMs = 50,
        };
        await using var worker = await SettlementWorkerHost.StartAsync(_fixture, fastRetry);
        worker.Clock.UtcNow = DateTimeOffset.UtcNow.AddMinutes(1);

        var paymentId = await CreatePaymentAsync();
        await AuthorizeInProcessAsync(paymentId);
        await CaptureAsync(paymentId);

        await Eventually(async () =>
        {
            var status = await ReadStatusAsync(paymentId);
            status.Should().Be("Settled");
        }, TimeSpan.FromSeconds(20));

        using var workerClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{worker.MetricsPort}") };
        var response = await workerClient.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().MatchRegex(
            "mq_consumed_total\\{queue=\"settlement\"\\} [1-9]\\d*",
            "the worker's own /metrics endpoint must surface mq_consumed_total — the whole point of Task 4");
    }

    [Fact]
    public async Task GetMetrics_AfterStatusGaugeUpdaterRuns_ExposesPaymentsByStatusGauge()
    {
        await SeedCapturedPaymentDirectlyAsync();

        await Eventually(async () =>
        {
            var response = await _client.GetAsync("/metrics");
            var body = await response.Content.ReadAsStringAsync();
            body.Should().MatchRegex(
                "payments_by_status\\{status=\"Captured\"\\} [1-9]\\d*",
                "the PaymentStatusGaugeUpdater BackgroundService must publish a non-zero gauge for captured payments");
        }, TimeSpan.FromSeconds(8));
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

    private async Task CaptureAsync(string paymentId)
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

    private async Task<string> SeedCapturedPaymentAsync(SettlementWorkerHost worker)
    {
        await using var scope = worker.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var payment = Payment.Create(
            merchantId: "mrc_acme",
            amount: new Money(12500L, "USD"),
            cardToken: "tok_visa_demo",
            customerReference: null,
            metadata: null,
            now: worker.Clock.UtcNow);

        var createdEvent = payment.CreateInitialEvent(worker.Clock.UtcNow);
        var authEvent = payment.Authorize(worker.Clock.UtcNow);
        var captureEvent = payment.Capture(worker.Clock.UtcNow);

        db.Payments.Add(payment);
        db.PaymentEvents.Add(createdEvent);
        db.PaymentEvents.Add(authEvent);
        db.PaymentEvents.Add(captureEvent);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private async Task SeedCapturedPaymentDirectlyAsync()
    {
        await using var scope = _apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var payment = Payment.Create(
            merchantId: "mrc_acme",
            amount: new Money(12500L, "USD"),
            cardToken: "tok_visa_demo",
            customerReference: null,
            metadata: null,
            now: clock.UtcNow);

        db.Payments.Add(payment);
        db.PaymentEvents.Add(payment.CreateInitialEvent(clock.UtcNow));
        db.PaymentEvents.Add(payment.Authorize(clock.UtcNow));
        db.PaymentEvents.Add(payment.Capture(clock.UtcNow));
        await db.SaveChangesAsync();
    }

    private static SettlePayment BuildSettleMessage(string paymentId, string correlationId, DateTimeOffset enqueuedAt) =>
        new(
            MessageId: "msg_" + Guid.NewGuid().ToString("N"),
            PaymentId: paymentId,
            MerchantId: "mrc_acme",
            AmountMinor: 12500L,
            Currency: "USD",
            CorrelationId: correlationId,
            Attempt: 1,
            EnqueuedAt: enqueuedAt);

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
