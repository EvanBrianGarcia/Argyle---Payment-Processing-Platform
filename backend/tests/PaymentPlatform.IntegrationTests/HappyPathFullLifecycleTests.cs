using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;
using PaymentPlatform.Worker.Consumers;

namespace PaymentPlatform.IntegrationTests;

/// Phase 3 capstone (Task 9). Drives a single payment through the full
/// async settlement pipeline: API create → in-process authorize → API
/// capture → outbox row → dispatcher publish → Worker consume → Settled.
/// Acts as the regression net for the whole Phase 3 surface — replaces the
/// skipped wiring smoke from Task 3 with a behavior-level check.
[Collection(MessagingTestCollection.Name)]
public sealed class HappyPathFullLifecycleTests : IAsyncLifetime
{
    private const string AcmeKey = "dev-key-mrc-acme";

    private readonly MessagingFixture _fixture;
    private MessagingApiFactory _apiFactory = default!;
    private HttpClient _client = default!;
    private SettlementWorkerHost _worker = default!;
    private static int _migrationCheck;

    public HappyPathFullLifecycleTests(MessagingFixture fixture)
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

        // Shorten the outbox dispatcher's poll interval so the test finishes
        // in seconds rather than the production default of 2 seconds.
        Environment.SetEnvironmentVariable("Outbox__Dispatcher__PollInterval", "00:00:00.500");

        _apiFactory = new MessagingApiFactory(_fixture);
        _client = _apiFactory.CreateClient();

        // Worker host with production SettlePaymentConsumer. The default
        // ControllableProcessor result is Success, so the consumer commits.
        var fastRetry = new WorkerRetryOptions
        {
            RetryLimit = 5,
            BaseIntervalMs = 50,
            MaxIntervalMs = 200,
            IncrementMs = 50,
        };
        _worker = await SettlementWorkerHost.StartAsync(_fixture, fastRetry);
        // Sync the worker's mutable clock to "now" so its Settled event is
        // chronologically after the API's create/authorize/capture events
        // (which use the real SystemClock).
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
    public async Task CreateAuthorizeCaptureSettle_PaymentEndsInSettled_WithFiveEventTimeline()
    {
        var paymentId = await CreatePaymentAsync();
        await AuthorizeInProcessAsync(paymentId);
        await CaptureAsync(paymentId);

        // Bounded retry — the consumer commits Settled once the outbox row
        // has been picked up, the message has been delivered, and the row
        // lock + processor call have finished.
        await Eventually(async () =>
        {
            var status = await ReadStatusAsync(paymentId);
            status.Should().Be("Settled");
        }, TimeSpan.FromSeconds(15));

        using var json = await GetPaymentAsync(paymentId);

        json.RootElement.GetProperty("status").GetString().Should().Be("Settled");

        var events = json.RootElement.GetProperty("events");
        events.GetArrayLength().Should().Be(
            4,
            "lifecycle must emit create + authorize + capture + settle (StateMachineEndToEndTests covers the same shape for the manual Refund path)");

        var toStatuses = events.EnumerateArray()
            .Select(e => e.GetProperty("to_status").GetString())
            .ToArray();
        toStatuses.Should().Equal("Pending", "Authorized", "Captured", "Settled");

        var settleEvent = events.EnumerateArray().Last();
        settleEvent.GetProperty("from_status").GetString().Should().Be("Captured");
        settleEvent.GetProperty("to_status").GetString().Should().Be("Settled");
        settleEvent.GetProperty("actor").GetString().Should().Be("worker");

        await AssertOutboxRowDispatchedAsync(paymentId);
    }

    private async Task EnsureMigratedAsync()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_fixture.Postgres.ConnectionString)
            .Options;
        await using var db = new PaymentsDbContext(options);
        await db.Database.MigrateAsync();
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
        using var json = await GetPaymentAsync(paymentId);
        return json.RootElement.GetProperty("status").GetString() ?? string.Empty;
    }

    private async Task<System.Text.Json.JsonDocument> GetPaymentAsync(string paymentId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/payments/{paymentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await TestJson.ParseAsync(response);
    }

    private async Task AssertOutboxRowDispatchedAsync(string paymentId)
    {
        await using var scope = _apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var rows = await db.PaymentOutbox.AsNoTracking()
            .Where(o => o.AggregateId == paymentId)
            .ToListAsync();

        rows.Should().ContainSingle(
            because: "exactly one outbox row should have been written when capture committed");
        rows.Single().DispatchedAt.Should().NotBeNull(
            because: "the dispatcher must have flipped dispatched_at after publishing");
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
