using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Domain.Common;
using PaymentPlatform.Domain.Idempotency;
using PaymentPlatform.Domain.Outbox;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests.Persistence;

[Collection(IntegrationTestCollection.Name)]
public sealed class MigrationSmokeTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 5, 15, 0, 0, TimeSpan.Zero);

    public MigrationSmokeTests(PostgresFixture postgres) : base(postgres)
    {
    }

    [Fact]
    public async Task PaymentEvent_RoundTrips_AllColumnsAndJsonbPayload()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var payment = SeedPayment(db);
        await db.SaveChangesAsync();

        var evt = PaymentEvent.Create(
            paymentId: payment.Id,
            fromStatus: PaymentStatus.Pending,
            toStatus: PaymentStatus.Authorized,
            actor: "system",
            reason: PaymentEventReason.AuthOk,
            payload: new Dictionary<string, string>
            {
                ["processor"] = "stub",
                ["latency_ms"] = "42",
            },
            at: Now);

        db.PaymentEvents.Add(evt);
        await db.SaveChangesAsync();

        // Reload from a fresh scope to confirm round-trip through Postgres.
        using var verifyScope = Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var loaded = await verifyDb.PaymentEvents
            .AsNoTracking()
            .SingleAsync(e => e.Id == evt.Id);

        loaded.PaymentId.Should().Be(payment.Id);
        loaded.FromStatus.Should().Be(PaymentStatus.Pending);
        loaded.ToStatus.Should().Be(PaymentStatus.Authorized);
        loaded.Actor.Should().Be("system");
        loaded.Reason.Should().Be(PaymentEventReason.AuthOk);
        loaded.At.Should().BeCloseTo(Now, TimeSpan.FromSeconds(1));
        loaded.Payload.Should().HaveCount(2);
        loaded.Payload["processor"].Should().Be("stub");
        loaded.Payload["latency_ms"].Should().Be("42");
    }

    [Fact]
    public async Task IdempotencyKey_PerOperation_AllowsSameMerchantAndKeyAcrossOperations()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        const string sharedKey = "f88a0f72-3a35-4f5b-9b16-7e8d4e93d5d4";

        var createRecord = new IdempotencyKeyRecord(
            merchantId: "mrc_acme",
            operation: "create_payment",
            key: sharedKey,
            requestHash: "hash_create",
            responseStatus: 201,
            responseBody: "{}",
            createdAt: Now);

        var captureRecord = new IdempotencyKeyRecord(
            merchantId: "mrc_acme",
            operation: "capture_payment",
            key: sharedKey,
            requestHash: "hash_capture",
            responseStatus: 200,
            responseBody: "{}",
            createdAt: Now);

        db.IdempotencyKeys.AddRange(createRecord, captureRecord);
        await db.SaveChangesAsync();

        using var verifyScope = Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var rows = await verifyDb.IdempotencyKeys
            .AsNoTracking()
            .Where(r => r.MerchantId == "mrc_acme" && r.Key == sharedKey)
            .OrderBy(r => r.Operation)
            .ToListAsync();

        rows.Should().HaveCount(2);
        rows.Select(r => r.Operation).Should().BeEquivalentTo(
            new[] { "capture_payment", "create_payment" },
            opts => opts.WithStrictOrdering());
    }

    [Fact]
    public async Task IdempotencyKey_DuplicateMerchantOperationKey_Throws()
    {
        const string key = "8a7c0a1a-6e29-4b3b-aae3-d2bb02b39b1d";

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            db.IdempotencyKeys.Add(new IdempotencyKeyRecord(
                merchantId: "mrc_acme",
                operation: "capture_payment",
                key: key,
                requestHash: "hash_first",
                responseStatus: 200,
                responseBody: "{}",
                createdAt: Now));
            await db.SaveChangesAsync();
        }

        using var secondScope = Factory.Services.CreateScope();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        secondDb.IdempotencyKeys.Add(new IdempotencyKeyRecord(
            merchantId: "mrc_acme",
            operation: "capture_payment",
            key: key,
            requestHash: "hash_second",
            responseStatus: 200,
            responseBody: "{}",
            createdAt: Now));

        var act = async () => await secondDb.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task PaymentOutbox_RoundTrips_AllColumnsIncludingJsonbPayloadAndCorrelationId()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var payment = SeedPayment(db);
        await db.SaveChangesAsync();

        var message = PaymentOutboxMessage.Create(
            aggregateId: payment.Id,
            messageType: OutboxMessageType.Settlement,
            payload: """{"payment_id":"pay_test","amount_minor":1000}""",
            correlationId: "trace-abc-123",
            createdAt: Now);

        db.PaymentOutbox.Add(message);
        await db.SaveChangesAsync();

        message.Id.Should().BeGreaterThan(0);

        using var verifyScope = Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var loaded = await verifyDb.PaymentOutbox
            .AsNoTracking()
            .SingleAsync(o => o.Id == message.Id);

        loaded.AggregateId.Should().Be(payment.Id);
        loaded.MessageType.Should().Be(OutboxMessageType.Settlement);
        loaded.Payload.Should().Contain("payment_id");
        loaded.Payload.Should().Contain("amount_minor");
        loaded.CorrelationId.Should().Be("trace-abc-123");
        loaded.CreatedAt.Should().BeCloseTo(Now, TimeSpan.FromSeconds(1));
        loaded.DispatchedAt.Should().BeNull();
    }

    [Fact]
    public async Task PaymentOutbox_PartialIndex_OnlyReturnsUndispatchedRowsForUndispatchedQueries()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var payment = SeedPayment(db);
        await db.SaveChangesAsync();

        var undispatched = PaymentOutboxMessage.Create(
            aggregateId: payment.Id,
            messageType: OutboxMessageType.Settlement,
            payload: "{}",
            correlationId: "trace-undispatched",
            createdAt: Now);

        var dispatched = PaymentOutboxMessage.Create(
            aggregateId: payment.Id,
            messageType: OutboxMessageType.Settlement,
            payload: "{}",
            correlationId: "trace-dispatched",
            createdAt: Now);

        db.PaymentOutbox.AddRange(undispatched, dispatched);
        await db.SaveChangesAsync();

        // Flip the second row's dispatched_at via ExecuteUpdateAsync — the same
        // pattern the OutboxDispatcher will use in Task 4.
        var dispatchedAt = Now.AddSeconds(5);
        await db.PaymentOutbox
            .Where(o => o.Id == dispatched.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.DispatchedAt, dispatchedAt));

        using var verifyScope = Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var undispatchedRows = await verifyDb.PaymentOutbox
            .AsNoTracking()
            .Where(o => o.DispatchedAt == null)
            .ToListAsync();

        undispatchedRows.Should().HaveCount(1);
        undispatchedRows[0].Id.Should().Be(undispatched.Id);

        var allRows = await verifyDb.PaymentOutbox
            .AsNoTracking()
            .OrderBy(o => o.Id)
            .ToListAsync();
        allRows.Should().HaveCount(2);
        allRows[1].DispatchedAt.Should().BeCloseTo(dispatchedAt, TimeSpan.FromSeconds(1));
    }

    private static Payment SeedPayment(IPaymentsDbContext db)
    {
        var payment = Payment.Create(
            merchantId: "mrc_acme",
            amount: new Money(1000L, "USD"),
            cardToken: "tok_stub_visa",
            customerReference: null,
            metadata: null,
            now: Now);

        db.Payments.Add(payment);
        return payment;
    }
}
