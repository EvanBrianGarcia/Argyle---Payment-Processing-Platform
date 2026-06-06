using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;
using PaymentPlatform.Messaging.Settlement;
using PaymentPlatform.Worker.Consumers;

namespace PaymentPlatform.IntegrationTests;

/// Phase 3 Task 6. Exercises the real SettlePaymentConsumer end-to-end
/// against a real Postgres database (FOR UPDATE row lock + tx + commit)
/// using MassTransit's in-memory ITestHarness so the bus path is fast.
public sealed class SettlementConsumerTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private SettlementConsumerHost _host = default!;
    private static int _migrationCheck;

    public SettlementConsumerTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    public async Task InitializeAsync()
    {
        if (Interlocked.CompareExchange(ref _migrationCheck, 1, 0) == 0)
        {
            await EnsureMigratedAsync();
        }
        await _postgres.ResetDatabaseAsync();
        _host = await SettlementConsumerHost.StartAsync(_postgres.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task HappyPath_CapturedPayment_TransitionsToSettled_AndAppendsWorkerEvent()
    {
        var paymentId = await SeedCapturedPaymentAsync();
        var settleAt = new DateTimeOffset(2026, 6, 1, 12, 30, 0, TimeSpan.Zero);
        _host.Clock.UtcNow = settleAt;

        await _host.Harness.Bus.Publish(BuildMessage(paymentId, "trace-happy"));
        await WaitForConsumedAsync();

        var payment = await LoadPaymentAsync(paymentId);
        payment.Status.Should().Be(PaymentStatus.Settled);
        payment.UpdatedAt.Should().Be(settleAt);

        var settlementEvents = await LoadSettlementEventsAsync(paymentId);
        settlementEvents.Should().ContainSingle();
        var settled = settlementEvents.Single();
        settled.FromStatus.Should().Be(PaymentStatus.Captured);
        settled.ToStatus.Should().Be(PaymentStatus.Settled);
        settled.Actor.Should().Be("worker");
        settled.At.Should().Be(settleAt);
    }

    [Fact]
    public async Task IdempotentRedelivery_SecondMessageIsAcked_WithoutSecondSettlementEvent()
    {
        var paymentId = await SeedCapturedPaymentAsync();
        var message = BuildMessage(paymentId, "trace-idempotent");

        await _host.Harness.Bus.Publish(message);
        await WaitForConsumedAsync();

        await _host.Harness.Bus.Publish(message);
        await WaitForConsumedAsync(expectedCount: 2);

        var payment = await LoadPaymentAsync(paymentId);
        payment.Status.Should().Be(PaymentStatus.Settled);

        var settlementEvents = await LoadSettlementEventsAsync(paymentId);
        settlementEvents.Should().ContainSingle(
            because: "the second delivery sees the payment already in Settled and short-circuits");

        _host.Processor.CallCount.Should().Be(
            1,
            because: "the second delivery returns before reaching the processor");

        await AssertNoConsumerExceptionsAsync();
    }

    [Fact]
    public async Task StateConflict_AlreadyRefundedPayment_AcksWithoutStateChange_AndDoesNotThrow()
    {
        var paymentId = await SeedRefundedPaymentAsync();

        await _host.Harness.Bus.Publish(BuildMessage(paymentId, "trace-state-conflict"));
        await WaitForConsumedAsync();

        var payment = await LoadPaymentAsync(paymentId);
        payment.Status.Should().Be(PaymentStatus.Refunded);

        var settlementEvents = await LoadSettlementEventsAsync(paymentId);
        settlementEvents.Should().BeEmpty();

        _host.Processor.CallCount.Should().Be(0);
        await AssertNoConsumerExceptionsAsync();
    }

    [Fact]
    public async Task TransientProcessorFailure_ConsumerThrows_NotAsPermanentFailureException()
    {
        var paymentId = await SeedCapturedPaymentAsync();
        _host.Processor.ResultFor = _ => new ProcessorResult.TransientFailure("network blip");

        await _host.Harness.Bus.Publish(BuildMessage(paymentId, "trace-transient"));
        await WaitForConsumedAsync();

        var thrown = await GetConsumerExceptionAsync();
        thrown.Should().NotBeNull();
        thrown.Should().NotBeOfType<PermanentSettlementFailureException>();
        thrown.Should().BeOfType<TransientSettlementException>();
        thrown!.Message.Should().Contain("network blip");

        var payment = await LoadPaymentAsync(paymentId);
        payment.Status.Should().Be(PaymentStatus.Captured);
        (await LoadSettlementEventsAsync(paymentId)).Should().BeEmpty();
    }

    [Fact]
    public async Task PermanentProcessorFailure_ConsumerThrowsPermanentSettlementFailureException()
    {
        var paymentId = await SeedCapturedPaymentAsync();
        _host.Processor.ResultFor = _ => new ProcessorResult.PermanentFailure("card_declined");

        await _host.Harness.Bus.Publish(BuildMessage(paymentId, "trace-permanent"));
        await WaitForConsumedAsync();

        var thrown = await GetConsumerExceptionAsync();
        thrown.Should().BeOfType<PermanentSettlementFailureException>();
        thrown!.Message.Should().Contain("card_declined");

        var payment = await LoadPaymentAsync(paymentId);
        payment.Status.Should().Be(PaymentStatus.Captured);
        (await LoadSettlementEventsAsync(paymentId)).Should().BeEmpty();
    }

    [Fact]
    public async Task PaymentNotFound_ConsumerThrowsPermanentSettlementFailureException()
    {
        await _host.Harness.Bus.Publish(BuildMessage("pay_does_not_exist", "trace-not-found"));
        await WaitForConsumedAsync();

        var thrown = await GetConsumerExceptionAsync();
        thrown.Should().BeOfType<PermanentSettlementFailureException>();
        thrown!.Message.Should().Contain("payment_not_found");
    }

    [Fact]
    public async Task ConcurrentDelivery_OnlyOneSettlementEventAppended_PaymentSettledExactlyOnce()
    {
        var paymentId = await SeedCapturedPaymentAsync();
        var message = BuildMessage(paymentId, "trace-concurrent");

        await Task.WhenAll(
            _host.Harness.Bus.Publish(message),
            _host.Harness.Bus.Publish(message));
        await WaitForConsumedAsync(expectedCount: 2);

        var payment = await LoadPaymentAsync(paymentId);
        payment.Status.Should().Be(PaymentStatus.Settled);

        var settlementEvents = await LoadSettlementEventsAsync(paymentId);
        settlementEvents.Should().ContainSingle(
            because: "FOR UPDATE serializes the two consumers; the loser sees Settled and skips");

        await AssertNoConsumerExceptionsAsync();
    }

    private async Task EnsureMigratedAsync()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_postgres.ConnectionString)
            .Options;
        await using var db = new PaymentsDbContext(options);
        await db.Database.MigrateAsync();
    }

    private async Task<string> SeedCapturedPaymentAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var payment = Payment.Create(
            merchantId: "mrc_acme",
            amount: new Money(12500L, "USD"),
            cardToken: "tok_visa_demo",
            customerReference: null,
            metadata: null,
            now: _host.Clock.UtcNow);

        var createdEvent = payment.CreateInitialEvent(_host.Clock.UtcNow);
        var authEvent = payment.Authorize(_host.Clock.UtcNow);
        var captureEvent = payment.Capture(_host.Clock.UtcNow);

        db.Payments.Add(payment);
        db.PaymentEvents.Add(createdEvent);
        db.PaymentEvents.Add(authEvent);
        db.PaymentEvents.Add(captureEvent);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private async Task<string> SeedRefundedPaymentAsync()
    {
        var paymentId = await SeedCapturedPaymentAsync();
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var payment = await db.Payments.FirstAsync(p => p.Id == paymentId);
        var refundEvent = payment.Refund(_host.Clock.UtcNow, "customer_requested");
        db.PaymentEvents.Add(refundEvent);
        await db.SaveChangesAsync();

        return paymentId;
    }

    private async Task<Payment> LoadPaymentAsync(string paymentId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
    }

    private async Task<IReadOnlyList<PaymentEvent>> LoadSettlementEventsAsync(string paymentId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.PaymentEvents.AsNoTracking()
            .Where(e => e.PaymentId == paymentId
                && e.FromStatus == PaymentStatus.Captured
                && e.ToStatus == PaymentStatus.Settled)
            .ToListAsync();
    }

    private async Task WaitForConsumedAsync(int expectedCount = 1)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await CountConsumedAsync() >= expectedCount)
            {
                return;
            }
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException(
            $"Timed out waiting for {expectedCount} consumed message(s); harness saw {await CountConsumedAsync()}.");
    }

    private async Task<int> CountConsumedAsync()
    {
        var count = 0;
        await foreach (var _ in _host.Harness.GetConsumerHarness<SettlePaymentConsumer>().Consumed
            .SelectAsync<SettlePayment>())
        {
            count++;
        }
        return count;
    }

    private async Task<Exception?> GetConsumerExceptionAsync()
    {
        await foreach (var consumed in _host.Harness.GetConsumerHarness<SettlePaymentConsumer>().Consumed
            .SelectAsync<SettlePayment>())
        {
            if (consumed.Exception is not null)
            {
                return consumed.Exception;
            }
        }
        return null;
    }

    private async Task AssertNoConsumerExceptionsAsync()
    {
        await foreach (var consumed in _host.Harness.GetConsumerHarness<SettlePaymentConsumer>().Consumed
            .SelectAsync<SettlePayment>())
        {
            consumed.Exception.Should().BeNull(because: "the consumer should ack without throwing");
        }
    }

    private SettlePayment BuildMessage(string paymentId, string correlationId) => new(
        MessageId: "msg_" + Guid.NewGuid().ToString("N"),
        PaymentId: paymentId,
        MerchantId: "mrc_acme",
        AmountMinor: 12500L,
        Currency: "USD",
        CorrelationId: correlationId,
        Attempt: 1,
        EnqueuedAt: _host.Clock.UtcNow);
}
