using System.Text.Json;
using FluentAssertions;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Domain.Outbox;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.UnitTests.Application.Outbox;

public sealed class OutboxMessageFactoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ForSettlement_BuildsOutboxRow_WithSerializedSettlePaymentPayload()
    {
        var payment = SeedPayment();

        var message = OutboxMessageFactory.ForSettlement(
            payment: payment,
            correlationId: "trace-abc-123",
            now: Now);

        message.AggregateId.Should().Be(payment.Id);
        message.MessageType.Should().Be(OutboxMessageType.Settlement);
        message.CorrelationId.Should().Be("trace-abc-123");
        message.CreatedAt.Should().Be(Now);
        message.DispatchedAt.Should().BeNull();

        var deserialized = JsonSerializer.Deserialize<SettlePayment>(message.Payload);
        deserialized.Should().NotBeNull();
        deserialized!.PaymentId.Should().Be(payment.Id);
        deserialized.MerchantId.Should().Be(payment.MerchantId);
        deserialized.AmountMinor.Should().Be(payment.Amount.AmountMinor);
        deserialized.Currency.Should().Be(payment.Amount.Currency);
        deserialized.CorrelationId.Should().Be("trace-abc-123");
        deserialized.Attempt.Should().Be(1);
        deserialized.EnqueuedAt.Should().Be(Now);
        deserialized.MessageId.Should().StartWith("msg_");
    }

    [Fact]
    public void ForSettlement_GeneratesDistinctMessageIds_AcrossCalls()
    {
        var payment = SeedPayment();

        var first = OutboxMessageFactory.ForSettlement(payment, "trace-a", Now);
        var second = OutboxMessageFactory.ForSettlement(payment, "trace-b", Now);

        var firstMessage = JsonSerializer.Deserialize<SettlePayment>(first.Payload)!;
        var secondMessage = JsonSerializer.Deserialize<SettlePayment>(second.Payload)!;

        firstMessage.MessageId.Should().NotBe(secondMessage.MessageId);
    }

    [Fact]
    public void ForSettlement_NullPayment_Throws()
    {
        var act = () => OutboxMessageFactory.ForSettlement(null!, "trace", Now);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForSettlement_BlankCorrelationId_Throws(string correlationId)
    {
        var payment = SeedPayment();
        var act = () => OutboxMessageFactory.ForSettlement(payment, correlationId, Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeserializeSettlement_RoundTripsFromOutboxRow()
    {
        var payment = SeedPayment();
        var message = OutboxMessageFactory.ForSettlement(payment, "trace-xyz", Now);

        var deserialized = OutboxMessageFactory.DeserializeSettlement(message);

        deserialized.PaymentId.Should().Be(payment.Id);
        deserialized.CorrelationId.Should().Be("trace-xyz");
    }

    private static Payment SeedPayment() => Payment.Create(
        merchantId: "mrc_acme",
        amount: new Money(12500L, "USD"),
        cardToken: "tok_stub_visa",
        customerReference: null,
        metadata: null,
        now: Now);
}
