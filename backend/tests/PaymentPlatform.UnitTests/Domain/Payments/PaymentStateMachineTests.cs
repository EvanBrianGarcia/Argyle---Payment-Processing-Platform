using FluentAssertions;
using PaymentPlatform.Domain.Common;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.UnitTests.Domain.Payments;

public class PaymentStateMachineTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 6, 5, 14, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Later =
        CreatedAt.AddMinutes(1);

    // Each row: (startingStatus, methodName, expectedTargetStatus, expectedEventReason)
    public static TheoryData<PaymentStatus, string, PaymentStatus, string> LegalTransitions => new()
    {
        { PaymentStatus.Pending, "Authorize", PaymentStatus.Authorized, PaymentEventReason.AuthOk },
        { PaymentStatus.Pending, "Fail", PaymentStatus.Failed, PaymentEventReason.Failed },
        { PaymentStatus.Authorized, "Capture", PaymentStatus.Captured, PaymentEventReason.Captured },
        { PaymentStatus.Authorized, "Fail", PaymentStatus.Failed, PaymentEventReason.Failed },
        { PaymentStatus.Captured, "Settle", PaymentStatus.Settled, PaymentEventReason.Settled },
        { PaymentStatus.Captured, "Refund", PaymentStatus.Refunded, PaymentEventReason.Refunded },
        { PaymentStatus.Settled, "Refund", PaymentStatus.Refunded, PaymentEventReason.Refunded },
    };

    public static TheoryData<PaymentStatus, string> IllegalTransitions
    {
        get
        {
            var legalSet = new HashSet<(PaymentStatus, string)>
            {
                (PaymentStatus.Pending, "Authorize"),
                (PaymentStatus.Pending, "Fail"),
                (PaymentStatus.Authorized, "Capture"),
                (PaymentStatus.Authorized, "Fail"),
                (PaymentStatus.Captured, "Settle"),
                (PaymentStatus.Captured, "Refund"),
                (PaymentStatus.Settled, "Refund"),
            };

            var allMethods = new[] { "Authorize", "Capture", "Refund", "Settle", "Fail" };
            var allStatuses = new[]
            {
                PaymentStatus.Pending,
                PaymentStatus.Authorized,
                PaymentStatus.Captured,
                PaymentStatus.Settled,
                PaymentStatus.Failed,
                PaymentStatus.Refunded,
            };

            var data = new TheoryData<PaymentStatus, string>();
            foreach (var status in allStatuses)
            {
                foreach (var method in allMethods)
                {
                    if (!legalSet.Contains((status, method)))
                    {
                        data.Add(status, method);
                    }
                }
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void LegalTransition_MutatesStatus_AndReturnsMatchingEvent(
        PaymentStatus from,
        string method,
        PaymentStatus to,
        string expectedReason)
    {
        var payment = PaymentInStatus(from);

        var evt = Invoke(payment, method, Later);

        payment.Status.Should().Be(to);
        evt.FromStatus.Should().Be(from);
        evt.ToStatus.Should().Be(to);
        evt.Reason.Should().Be(expectedReason);
        evt.PaymentId.Should().Be(payment.Id);
        evt.At.Should().Be(Later);
    }

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void LegalTransition_AdvancesUpdatedAt(
        PaymentStatus from,
        string method,
        PaymentStatus to,
        string expectedReason)
    {
        _ = to;
        _ = expectedReason;
        var payment = PaymentInStatus(from);
        var updatedBefore = payment.UpdatedAt;

        Invoke(payment, method, Later);

        payment.UpdatedAt.Should().Be(Later);
        payment.UpdatedAt.Should().BeAfter(updatedBefore);
    }

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public void IllegalTransition_Throws_AndPreservesStatus(
        PaymentStatus from,
        string method)
    {
        var payment = PaymentInStatus(from);
        var statusBefore = payment.Status;
        var updatedBefore = payment.UpdatedAt;

        var act = () => Invoke(payment, method, Later);

        act.Should().Throw<InvalidTransitionException>()
            .Which.Code.Should().Be("invalid_state_transition");

        payment.Status.Should().Be(statusBefore);
        payment.UpdatedAt.Should().Be(updatedBefore);
    }

    [Fact]
    public void InvalidTransition_ExceptionCarriesFromAndToHints()
    {
        var payment = PaymentInStatus(PaymentStatus.Captured);

        var act = () => payment.Authorize(Later);

        var thrown = act.Should().Throw<InvalidTransitionException>().Which;
        thrown.From.Should().Be(PaymentStatus.Captured);
        thrown.To.Should().Be(PaymentStatus.Authorized);
    }

    [Fact]
    public void Refund_FromCaptured_PreservesReasonInPayload()
    {
        var payment = PaymentInStatus(PaymentStatus.Captured);

        var evt = payment.Refund(Later, reason: "customer_request");

        evt.Payload.Should().ContainKey("reason").WhoseValue.Should().Be("customer_request");
    }

    [Fact]
    public void Fail_PreservesReasonInPayload()
    {
        var payment = PaymentInStatus(PaymentStatus.Pending);

        var evt = payment.Fail(Later, reason: "auth_declined");

        evt.Payload.Should().ContainKey("reason").WhoseValue.Should().Be("auth_declined");
    }

    [Fact]
    public void CreateInitialEvent_ReturnsNullToPendingEvent()
    {
        var payment = Payment.Create(
            merchantId: "mrc_1",
            amount: new Money(1000, "USD"),
            cardToken: "tok_x",
            customerReference: null,
            metadata: null,
            now: CreatedAt);

        var evt = payment.CreateInitialEvent(CreatedAt);

        evt.FromStatus.Should().BeNull();
        evt.ToStatus.Should().Be(PaymentStatus.Pending);
        evt.Reason.Should().Be(PaymentEventReason.Created);
        evt.PaymentId.Should().Be(payment.Id);
        evt.At.Should().Be(CreatedAt);
    }

    private static Payment PaymentInStatus(PaymentStatus target)
    {
        var payment = Payment.Create(
            merchantId: "mrc_1",
            amount: new Money(1000, "USD"),
            cardToken: "tok_x",
            customerReference: null,
            metadata: null,
            now: CreatedAt);

        switch (target)
        {
            case PaymentStatus.Pending:
                return payment;
            case PaymentStatus.Authorized:
                payment.Authorize(CreatedAt);
                return payment;
            case PaymentStatus.Captured:
                payment.Authorize(CreatedAt);
                payment.Capture(CreatedAt);
                return payment;
            case PaymentStatus.Settled:
                payment.Authorize(CreatedAt);
                payment.Capture(CreatedAt);
                payment.Settle(CreatedAt);
                return payment;
            case PaymentStatus.Failed:
                payment.Fail(CreatedAt, reason: "setup");
                return payment;
            case PaymentStatus.Refunded:
                payment.Authorize(CreatedAt);
                payment.Capture(CreatedAt);
                payment.Refund(CreatedAt, reason: "setup");
                return payment;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private static PaymentEvent Invoke(Payment payment, string method, DateTimeOffset at) =>
        method switch
        {
            "Authorize" => payment.Authorize(at),
            "Capture" => payment.Capture(at),
            "Refund" => payment.Refund(at, reason: "test"),
            "Settle" => payment.Settle(at),
            "Fail" => payment.Fail(at, reason: "test"),
            _ => throw new ArgumentException($"Unknown method '{method}'", nameof(method)),
        };
}
