using FluentAssertions;
using PaymentPlatform.Domain.Common;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.UnitTests.Domain.Payments;

public class PaymentEventTests
{
    private static readonly DateTimeOffset At =
        new(2026, 6, 5, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_HappyPath_ReturnsEventWithGeneratedId()
    {
        var evt = PaymentEvent.Create(
            paymentId: "pay_123",
            fromStatus: PaymentStatus.Pending,
            toStatus: PaymentStatus.Authorized,
            actor: "system",
            reason: PaymentEventReason.AuthOk,
            payload: null,
            at: At);

        evt.Id.Should().StartWith("evt_");
        evt.PaymentId.Should().Be("pay_123");
        evt.FromStatus.Should().Be(PaymentStatus.Pending);
        evt.ToStatus.Should().Be(PaymentStatus.Authorized);
        evt.Actor.Should().Be("system");
        evt.Reason.Should().Be(PaymentEventReason.AuthOk);
        evt.At.Should().Be(At);
        evt.Payload.Should().NotBeNull();
        evt.Payload.Should().BeEmpty();
    }

    [Fact]
    public void Create_CreationEvent_AllowsNullFromStatus()
    {
        var evt = PaymentEvent.Create(
            paymentId: "pay_123",
            fromStatus: null,
            toStatus: PaymentStatus.Pending,
            actor: "api",
            reason: PaymentEventReason.Created,
            payload: null,
            at: At);

        evt.FromStatus.Should().BeNull();
        evt.ToStatus.Should().Be(PaymentStatus.Pending);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankActor_Throws(string? actor)
    {
        var act = () => PaymentEvent.Create(
            "pay_1",
            PaymentStatus.Pending,
            PaymentStatus.Authorized,
            actor!,
            PaymentEventReason.AuthOk,
            payload: null,
            at: At);

        act.Should().Throw<DomainException>().Which.Code.Should().Be("invalid_event_actor");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankReason_Throws(string? reason)
    {
        var act = () => PaymentEvent.Create(
            "pay_1",
            PaymentStatus.Pending,
            PaymentStatus.Authorized,
            "system",
            reason!,
            payload: null,
            at: At);

        act.Should().Throw<DomainException>().Which.Code.Should().Be("invalid_event_reason");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankPaymentId_Throws(string? paymentId)
    {
        var act = () => PaymentEvent.Create(
            paymentId!,
            PaymentStatus.Pending,
            PaymentStatus.Authorized,
            "system",
            PaymentEventReason.AuthOk,
            payload: null,
            at: At);

        act.Should().Throw<DomainException>().Which.Code.Should().Be("invalid_payment_id");
    }

    [Fact]
    public void Create_PreservesPayload()
    {
        var payload = new Dictionary<string, string>
        {
            ["reason"] = "customer_request",
            ["amount_minor"] = "5000",
        };

        var evt = PaymentEvent.Create(
            "pay_1",
            PaymentStatus.Captured,
            PaymentStatus.Refunded,
            "api",
            PaymentEventReason.Refunded,
            payload: payload,
            at: At);

        evt.Payload.Should().HaveCount(2);
        evt.Payload["reason"].Should().Be("customer_request");
        evt.Payload["amount_minor"].Should().Be("5000");
    }
}
