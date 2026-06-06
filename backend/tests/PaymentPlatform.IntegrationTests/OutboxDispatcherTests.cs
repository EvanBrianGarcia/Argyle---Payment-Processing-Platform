using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Domain.Outbox;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.IntegrationTests;

/// Phase 3 Task 4. Asserts the OutboxDispatcher BackgroundService picks up
/// undispatched payment_outbox rows, publishes via MassTransit, and flips
/// dispatched_at. Uses the real RabbitMqFixture (plus a Worker test host
/// recording received messages) so the assertions exercise the full path.
public sealed class OutboxDispatcherTests : IAsyncLifetime
{
    private readonly MessagingFixture _fixture = new();
    private MessagingApiFactory _apiFactory = default!;
    private WorkerTestHost _worker = default!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await _fixture.Postgres.ResetDatabaseAsync();

        // Shorten the poll interval so tests finish in seconds, not minutes.
        Environment.SetEnvironmentVariable("Outbox__Dispatcher__PollInterval", "00:00:00.500");

        _apiFactory = new MessagingApiFactory(_fixture);
        _ = _apiFactory.CreateClient();

        _worker = await WorkerTestHost.StartAsync(_fixture);
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("Outbox__Dispatcher__PollInterval", null);
        await _worker.DisposeAsync();
        _apiFactory.Dispose();
        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task UndispatchedRow_GetsPublishedAndFlippedDispatchedAt_WithinPollInterval()
    {
        var (paymentId, message) = await InsertUndispatchedSettlementAsync("trace-happy-path");

        var received = await _worker.Sink.WaitForNextAsync(TimeSpan.FromSeconds(10));

        received.Should().NotBeNull();
        received!.PaymentId.Should().Be(paymentId);

        // Allow the dispatcher one more tick to flip dispatched_at after the publish.
        await Eventually(async () =>
        {
            using var verifyScope = _apiFactory.Services.CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var row = await verifyDb.PaymentOutbox.AsNoTracking()
                .SingleAsync(o => o.Id == message.Id);
            row.DispatchedAt.Should().NotBeNull();
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CorrelationId_IsPropagated_FromOutboxRowToConsumedMessage()
    {
        const string correlationId = "trace-correlation-propagation-123";
        var (_, _) = await InsertUndispatchedSettlementAsync(correlationId);

        var received = await _worker.Sink.WaitForNextAsync(TimeSpan.FromSeconds(10));

        received.Should().NotBeNull();
        received!.CorrelationId.Should().Be(correlationId);
    }

    [Fact]
    public async Task MultipleRows_ArePublished_InCreatedAtAscendingOrder()
    {
        var first = await InsertUndispatchedSettlementAsync(
            correlationId: "trace-first",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-3));
        var second = await InsertUndispatchedSettlementAsync(
            correlationId: "trace-second",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        var third = await InsertUndispatchedSettlementAsync(
            correlationId: "trace-third",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        // Wait for all three to land in the sink.
        await Eventually(() =>
        {
            _worker.Sink.Received.Should().HaveCountGreaterThanOrEqualTo(3);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(15));

        var orderedCorrelations = _worker.Sink.Received
            .Select(m => m.CorrelationId)
            .Take(3)
            .ToList();

        orderedCorrelations.Should().Equal("trace-first", "trace-second", "trace-third");
    }

    private async Task<(string PaymentId, PaymentOutboxMessage Row)> InsertUndispatchedSettlementAsync(
        string correlationId,
        DateTimeOffset? createdAt = null)
    {
        using var scope = _apiFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var payment = Payment.Create(
            merchantId: "mrc_acme",
            amount: new Money(12500L, "USD"),
            cardToken: "tok_stub_visa",
            customerReference: null,
            metadata: null,
            now: DateTimeOffset.UtcNow);
        db.Payments.Add(payment);

        var message = OutboxMessageFactory.ForSettlement(
            payment: payment,
            correlationId: correlationId,
            now: createdAt ?? DateTimeOffset.UtcNow);
        db.PaymentOutbox.Add(message);

        await db.SaveChangesAsync();
        return (payment.Id, message);
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
