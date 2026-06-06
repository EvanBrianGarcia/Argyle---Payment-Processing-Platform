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

/// Phase 3 Task 7. Exercises the dead-letter path against a real RabbitMQ
/// broker. PermanentSettlementFailureException is in the retry policy's
/// Ignore<> set, so the message must skip the retry budget, publish a
/// Fault<SettlePayment> event, and leave the payment in Captured.
[Collection(MessagingTestCollection.Name)]
public sealed class SettlementDeadLetterTests : IAsyncLifetime
{
    private readonly MessagingFixture _fixture;
    private SettlementWorkerHost _worker = default!;
    private static int _migrationCheck;

    public SettlementDeadLetterTests(MessagingFixture fixture)
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
    public async Task PermanentFailure_BypassesRetry_PublishesFault_AndLeavesPaymentInCaptured()
    {
        var paymentId = await SeedCapturedPaymentAsync();
        _worker.Processor.ResultFor = _ => new ProcessorResult.PermanentFailure("card_declined");

        await using var scope = _worker.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();
        await bus.Publish(BuildMessage(paymentId, "trace-permanent"));

        // Diagnostic: wait briefly, then assert the consumer was invoked so
        // a later fault-sink timeout points at observer wiring, not delivery.
        var processorReached = await WaitForConditionAsync(
            () => _worker.Processor.CallCount > 0,
            TimeSpan.FromSeconds(30));
        processorReached.Should().BeTrue(
            $"the consumer should have been invoked but CallCount stayed at {_worker.Processor.CallCount}");

        var dead = await _worker.Faults.WaitForNextAsync(TimeSpan.FromSeconds(15));
        dead.Should().NotBeNull(
            because: "the consume-fault pipeline fires when PermanentSettlementFailureException propagates past Ignore<>");

        dead!.Value.Message.PaymentId.Should().Be(paymentId);
        dead.Value.Exception.Should().BeOfType<PermanentSettlementFailureException>();
        dead.Value.Exception.Message.Should().Contain("card_declined");

        _worker.Processor.CallCount.Should().Be(
            1,
            because: "the permanent-failure exception is in the Ignore<> list, so MassTransit does not retry");

        var payment = await LoadPaymentAsync(paymentId);
        payment.Status.Should().Be(
            PaymentStatus.Captured,
            because: "a permanent settlement failure must leave the payment in its prior state");

        var settlementEvents = await LoadSettlementEventsAsync(paymentId);
        settlementEvents.Should().BeEmpty(
            because: "no settlement event is appended when the consumer rolls back");
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

    private static async Task<bool> WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }
            await Task.Delay(100);
        }
        return predicate();
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
}
