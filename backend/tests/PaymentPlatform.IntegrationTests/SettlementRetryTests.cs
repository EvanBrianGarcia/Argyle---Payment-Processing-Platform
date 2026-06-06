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

namespace PaymentPlatform.IntegrationTests;

/// Phase 3 Task 7. Exercises the MassTransit retry policy against a real
/// RabbitMQ broker. The processor is configured to fail twice transiently
/// before succeeding — the consumer must retry, succeed on the third call,
/// and append exactly one settlement event.
[Collection(MessagingTestCollection.Name)]
public sealed class SettlementRetryTests : IAsyncLifetime
{
    private readonly MessagingFixture _fixture;
    private SettlementWorkerHost _worker = default!;
    private static int _migrationCheck;

    public SettlementRetryTests(MessagingFixture fixture)
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

        var fastRetry = new WorkerRetryOptions
        {
            RetryLimit = 5,
            BaseIntervalMs = 50,
            MaxIntervalMs = 200,
            IncrementMs = 50,
        };

        _worker = await SettlementWorkerHost.StartAsync(_fixture, fastRetry);
    }

    public async Task DisposeAsync()
    {
        await _worker.DisposeAsync();
    }

    [Fact]
    public async Task TransientFailures_AreRetried_PaymentSettlesAfterThirdAttempt()
    {
        var paymentId = await SeedCapturedPaymentAsync();

        var attempts = 0;
        _worker.Processor.ResultFor = _ =>
        {
            var current = Interlocked.Increment(ref attempts);
            return current <= 2
                ? new ProcessorResult.TransientFailure($"transient_attempt_{current}")
                : new ProcessorResult.Success("stub_ref_after_retry");
        };

        await using var scope = _worker.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();
        await bus.Publish(BuildMessage(paymentId, "trace-retry"));

        await Eventually(async () =>
        {
            var payment = await LoadPaymentAsync(paymentId);
            payment.Status.Should().Be(
                PaymentStatus.Settled,
                because: $"the retry policy should reach the third attempt (CallCount={_worker.Processor.CallCount})");
        }, TimeSpan.FromSeconds(30));

        _worker.Processor.CallCount.Should().Be(
            3,
            because: "the processor should run once for the original delivery and twice for the retries");

        var settlementEvents = await LoadSettlementEventsAsync(paymentId);
        settlementEvents.Should().ContainSingle(
            because: "only the third (successful) attempt commits the transaction");

        _worker.Faults.Received.Should().BeEmpty(
            because: "transient failures that eventually succeed should not publish a fault");
    }

    private async Task EnsureMigratedAsync()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_fixture.Postgres.ConnectionString)
            .Options;
        await using var db = new PaymentsDbContext(options);
        await db.Database.MigrateAsync();
    }

    private async Task<string> SeedCapturedPaymentAsync()
    {
        await using var scope = _worker.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var payment = Payment.Create(
            merchantId: "mrc_acme",
            amount: new Money(12500L, "USD"),
            cardToken: "tok_visa_demo",
            customerReference: null,
            metadata: null,
            now: _worker.Clock.UtcNow);

        var createdEvent = payment.CreateInitialEvent(_worker.Clock.UtcNow);
        var authEvent = payment.Authorize(_worker.Clock.UtcNow);
        var captureEvent = payment.Capture(_worker.Clock.UtcNow);

        db.Payments.Add(payment);
        db.PaymentEvents.Add(createdEvent);
        db.PaymentEvents.Add(authEvent);
        db.PaymentEvents.Add(captureEvent);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private async Task<Payment> LoadPaymentAsync(string paymentId)
    {
        await using var scope = _worker.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
    }

    private async Task<IReadOnlyList<PaymentEvent>> LoadSettlementEventsAsync(string paymentId)
    {
        await using var scope = _worker.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.PaymentEvents.AsNoTracking()
            .Where(e => e.PaymentId == paymentId
                && e.FromStatus == PaymentStatus.Captured
                && e.ToStatus == PaymentStatus.Settled)
            .ToListAsync();
    }

    private SettlePayment BuildMessage(string paymentId, string correlationId) => new(
        MessageId: "msg_" + Guid.NewGuid().ToString("N"),
        PaymentId: paymentId,
        MerchantId: "mrc_acme",
        AmountMinor: 12500L,
        Currency: "USD",
        CorrelationId: correlationId,
        Attempt: 1,
        EnqueuedAt: _worker.Clock.UtcNow);

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
