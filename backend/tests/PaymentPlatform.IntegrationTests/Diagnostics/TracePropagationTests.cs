using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using MassTransit.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Diagnostics;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests.Diagnostics;

/// Phase 4 Task 1 — proves the OpenTelemetry SDK is wired in both the API
/// and the Worker, traceparent rides across the AMQP boundary, and excluded
/// paths (/health, /metrics) do not emit spans.
///
/// Uses MessagingTestCollection for the shared Postgres + RabbitMQ
/// containers and an InMemoryTelemetryFixture class-fixture for span
/// capture.
[Collection(MessagingTestCollection.Name)]
public sealed class TracePropagationTests : IClassFixture<InMemoryTelemetryFixture>, IAsyncLifetime
{
    private const string AcmeKey = "dev-key-mrc-acme";

    private readonly MessagingFixture _fixture;
    private readonly InMemoryTelemetryFixture _telemetry;
    private MessagingApiFactory _apiFactory = default!;
    private HttpClient _client = default!;
    private SettlementWorkerHost _worker = default!;
    private static int _migrationCheck;

    public TracePropagationTests(MessagingFixture fixture, InMemoryTelemetryFixture telemetry)
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

        Environment.SetEnvironmentVariable("Outbox__Dispatcher__PollInterval", "00:00:00.500");

        _apiFactory = new MessagingApiFactory(_fixture);
        _client = _apiFactory.CreateClient();

        var fastRetry = new global::PaymentPlatform.Worker.Consumers.WorkerRetryOptions
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
        Environment.SetEnvironmentVariable("Outbox__Dispatcher__PollInterval", null);
        await _worker.DisposeAsync();
        _client.Dispose();
        _apiFactory.Dispose();
    }

    [Fact]
    public async Task POST_Payments_EmitsServerSpan_WithW3CTraceId()
    {
        var beforeCount = _telemetry.Captured.Count;

        await CreatePaymentAsync();

        var traceId = await AwaitTraceIdForNewServerSpanAsync(beforeCount, "POST /v1/payments");
        var traceSpans = _telemetry.SnapshotForTrace(traceId);

        traceSpans.Should().Contain(
            a => a.Kind == ActivityKind.Server,
            "the ASP.NET Core auto-instrumentation must emit a server span for POST /v1/payments");

        traceSpans.Should().Contain(
            a => a.Source.Name == PaymentsActivitySource.Name && a.OperationName == "CreatePayment.Handle",
            "CreatePaymentCommandHandler must wrap its work in a PaymentsActivitySource span");

        // W3C trace ids are 32 hex characters.
        traceId.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task Capture_Then_Settlement_LinksConsumeSpanToPublishSpan()
    {
        var paymentId = await CreatePaymentAsync();
        await AuthorizeInProcessAsync(paymentId);

        var beforeCapture = _telemetry.Captured.Count;
        await CaptureAsync(paymentId);
        var captureTraceId = await AwaitTraceIdForNewServerSpanAsync(
            beforeCapture, $"POST /v1/payments/{paymentId}/capture");

        // Wait for settlement to land.
        await Eventually(async () =>
        {
            var status = await ReadStatusAsync(paymentId);
            status.Should().Be("Settled");
        }, TimeSpan.FromSeconds(20));

        // Wait for the consumer-side span to drain.
        await WaitForConsumerSpanAsync(paymentId);

        var freshSpans = _telemetry.Captured.Skip(beforeCapture).ToArray();
        var captureTraceSpans = freshSpans
            .Where(a => string.Equals(a.TraceId.ToString(), captureTraceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        captureTraceSpans.Should().Contain(
            a => a.Kind == ActivityKind.Server,
            "the API capture call must emit a server span");

        var consumerSpan = freshSpans.FirstOrDefault(a => a.Kind == ActivityKind.Consumer);
        consumerSpan.Should().NotBeNull(
            "MassTransit must emit a consumer span for SettlePayment");

        // The producer side lives in the OutboxDispatcher's background trace
        // (different root from the capture trace until the outbox row carries
        // a stored traceparent — Phase 4 Task 5 territory). The cross-process
        // claim is consume.ParentSpanId points at a span in the SAME trace
        // emitted on the publishing side (MassTransit's source).
        consumerSpan!.ParentSpanId.ToString().Should().NotMatch("0000000000000000",
            "the consume span must have a parent — that is the cross-process traceparent linkage");

        var consumerTraceSpans = freshSpans
            .Where(a => a.TraceId == consumerSpan.TraceId)
            .ToArray();
        var parentOfConsumer = consumerTraceSpans
            .FirstOrDefault(a => a.SpanId == consumerSpan.ParentSpanId);
        parentOfConsumer.Should().NotBeNull(
            "the consume span's parent must be captured in the same trace — that is the W3C traceparent that rode across the AMQP envelope");
        parentOfConsumer!.Source.Name.Should().Be(
            DiagnosticHeaders.DefaultListenerName,
            "the consume span's parent must be a MassTransit-emitted publish span");

        freshSpans.Should().Contain(
            a => a.Source.Name == PaymentsActivitySource.Name && a.OperationName == "Settlement.Consume",
            "SettlePaymentConsumer must wrap its work in an app-level activity");
    }

    private async Task WaitForConsumerSpanAsync(string paymentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (_telemetry.Captured.Any(a => a.Kind == ActivityKind.Consumer))
            {
                return;
            }
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task HealthReady_DoesNotEmitSpan()
    {
        var beforeCount = _telemetry.Captured.Count;

        var response = await _client.GetAsync("/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Give the SDK a beat in case a span was about to drain.
        await Task.Delay(250);

        var newSpans = _telemetry.Captured.Skip(beforeCount).ToArray();
        newSpans.Should().NotContain(
            a => a.Kind == ActivityKind.Server && IsHealthPath(a),
            "/health endpoints must be excluded from the AspNetCore instrumentation Filter");
    }

    [Fact]
    public async Task Metrics_DoesNotEmitSpan()
    {
        var beforeCount = _telemetry.Captured.Count;

        // /metrics doesn't exist yet (Task 2 wires it). Whatever status we
        // get back, the AspNetCore Filter must already exclude that route so
        // Task 2 inherits a clean trace stream.
        await _client.GetAsync("/metrics");

        await Task.Delay(250);

        var newSpans = _telemetry.Captured.Skip(beforeCount).ToArray();
        newSpans.Should().NotContain(
            a => a.Kind == ActivityKind.Server && IsMetricsPath(a),
            "/metrics must be excluded from the AspNetCore instrumentation Filter");
    }

    private static bool IsHealthPath(Activity activity)
    {
        var path = (activity.GetTagItem("url.path") as string)
            ?? (activity.GetTagItem("http.target") as string);
        return path is not null && path.StartsWith("/health", StringComparison.Ordinal);
    }

    private static bool IsMetricsPath(Activity activity)
    {
        var path = (activity.GetTagItem("url.path") as string)
            ?? (activity.GetTagItem("http.target") as string);
        return path is not null && path.StartsWith("/metrics", StringComparison.Ordinal);
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

    private async Task WaitForSpansAsync(
        string traceId,
        Func<Activity, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (_telemetry.SnapshotForTrace(traceId).Any(predicate))
            {
                return;
            }
            await Task.Delay(100);
        }
    }

    /// Block until a server span appears in Captured that matches the
    /// request shape, then return its W3C trace id. Task 5 will add a
    /// response `traceparent` header that makes this lookup unnecessary;
    /// for Task 1 we sniff the captured spans directly.
    private async Task<string> AwaitTraceIdForNewServerSpanAsync(
        int beforeCount,
        string displayHint,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var fresh = _telemetry.Captured.Skip(beforeCount).ToArray();
            var serverSpan = fresh.FirstOrDefault(a => a.Kind == ActivityKind.Server);
            if (serverSpan is not null)
            {
                return serverSpan.TraceId.ToString();
            }
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException(
            $"No server span appeared after the {displayHint} request — ASP.NET Core instrumentation likely is not registered.");
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
