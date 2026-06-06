using PaymentPlatform.Domain.Common;

namespace PaymentPlatform.Domain.Payments;

public sealed class PaymentEvent
{
    public string Id { get; private set; } = default!;
    public string PaymentId { get; private set; } = default!;
    public PaymentStatus? FromStatus { get; private set; }
    public PaymentStatus ToStatus { get; private set; }
    public string Actor { get; private set; } = default!;
    public string Reason { get; private set; } = default!;
    public IReadOnlyDictionary<string, string> Payload { get; private set; } = default!;
    public DateTimeOffset At { get; private set; }

    private PaymentEvent()
    {
    }

    private PaymentEvent(
        string id,
        string paymentId,
        PaymentStatus? fromStatus,
        PaymentStatus toStatus,
        string actor,
        string reason,
        IReadOnlyDictionary<string, string> payload,
        DateTimeOffset at)
    {
        Id = id;
        PaymentId = paymentId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Actor = actor;
        Reason = reason;
        Payload = payload;
        At = at;
    }

    public static PaymentEvent Create(
        string paymentId,
        PaymentStatus? fromStatus,
        PaymentStatus toStatus,
        string actor,
        string reason,
        IReadOnlyDictionary<string, string>? payload,
        DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(paymentId))
        {
            throw new DomainException("invalid_payment_id", "Payment id must be non-blank.");
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new DomainException("invalid_event_actor", "Event actor must be non-blank.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("invalid_event_reason", "Event reason must be non-blank.");
        }

        return new PaymentEvent(
            id: IdGenerator.NewEventId(),
            paymentId: paymentId,
            fromStatus: fromStatus,
            toStatus: toStatus,
            actor: actor,
            reason: reason,
            payload: payload ?? new Dictionary<string, string>(0),
            at: at);
    }
}
