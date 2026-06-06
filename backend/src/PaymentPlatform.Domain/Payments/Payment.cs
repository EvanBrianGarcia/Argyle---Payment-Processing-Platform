using PaymentPlatform.Domain.Common;

namespace PaymentPlatform.Domain.Payments;

public sealed class Payment
{
    public string Id { get; private set; } = default!;
    public string MerchantId { get; private set; } = default!;
    public Money Amount { get; private set; }
    public string CardToken { get; private set; } = default!;
    public string? CustomerReference { get; private set; }
    public IReadOnlyDictionary<string, string> Metadata { get; private set; } = default!;
    public PaymentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Payment()
    {
    }

    private Payment(
        string id,
        string merchantId,
        Money amount,
        string cardToken,
        string? customerReference,
        IReadOnlyDictionary<string, string> metadata,
        PaymentStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        MerchantId = merchantId;
        Amount = amount;
        CardToken = cardToken;
        CustomerReference = customerReference;
        Metadata = metadata;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public static Payment Create(
        string merchantId,
        Money amount,
        string cardToken,
        string? customerReference,
        IReadOnlyDictionary<string, string>? metadata,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            throw new DomainException("invalid_merchant", "Merchant id must be non-blank.");
        }

        if (string.IsNullOrWhiteSpace(cardToken))
        {
            throw new DomainException("invalid_card_token", "Card token must be non-blank.");
        }

        var safeMetadata = metadata ?? new Dictionary<string, string>(0);

        return new Payment(
            id: IdGenerator.NewPaymentId(),
            merchantId: merchantId,
            amount: amount,
            cardToken: cardToken,
            customerReference: customerReference,
            metadata: safeMetadata,
            status: PaymentStatus.Pending,
            createdAt: now,
            updatedAt: now);
    }

    public PaymentEvent CreateInitialEvent(DateTimeOffset at, string actor = "api") =>
        PaymentEvent.Create(
            paymentId: Id,
            fromStatus: null,
            toStatus: Status,
            actor: actor,
            reason: PaymentEventReason.Created,
            payload: null,
            at: at);

    public PaymentEvent Authorize(DateTimeOffset now, string actor = "system") =>
        Transition(
            allowedFrom: PaymentStatus.Pending,
            to: PaymentStatus.Authorized,
            reason: PaymentEventReason.AuthOk,
            payload: null,
            actor: actor,
            now: now);

    public PaymentEvent Capture(DateTimeOffset now, string actor = "api") =>
        Transition(
            allowedFrom: PaymentStatus.Authorized,
            to: PaymentStatus.Captured,
            reason: PaymentEventReason.Captured,
            payload: null,
            actor: actor,
            now: now);

    public PaymentEvent Settle(DateTimeOffset now, string actor = "worker") =>
        Transition(
            allowedFrom: PaymentStatus.Captured,
            to: PaymentStatus.Settled,
            reason: PaymentEventReason.Settled,
            payload: null,
            actor: actor,
            now: now);

    public PaymentEvent Refund(DateTimeOffset now, string reason, string actor = "api")
    {
        if (Status != PaymentStatus.Captured && Status != PaymentStatus.Settled)
        {
            throw new InvalidTransitionException(Status, PaymentStatus.Refunded);
        }

        var fromStatus = Status;
        Status = PaymentStatus.Refunded;
        UpdatedAt = now;

        return PaymentEvent.Create(
            paymentId: Id,
            fromStatus: fromStatus,
            toStatus: PaymentStatus.Refunded,
            actor: actor,
            reason: PaymentEventReason.Refunded,
            payload: ReasonPayload(reason),
            at: now);
    }

    public PaymentEvent Fail(DateTimeOffset now, string reason, string actor = "system")
    {
        if (Status != PaymentStatus.Pending && Status != PaymentStatus.Authorized)
        {
            throw new InvalidTransitionException(Status, PaymentStatus.Failed);
        }

        var fromStatus = Status;
        Status = PaymentStatus.Failed;
        UpdatedAt = now;

        return PaymentEvent.Create(
            paymentId: Id,
            fromStatus: fromStatus,
            toStatus: PaymentStatus.Failed,
            actor: actor,
            reason: PaymentEventReason.Failed,
            payload: ReasonPayload(reason),
            at: now);
    }

    private PaymentEvent Transition(
        PaymentStatus allowedFrom,
        PaymentStatus to,
        string reason,
        IReadOnlyDictionary<string, string>? payload,
        string actor,
        DateTimeOffset now)
    {
        if (Status != allowedFrom)
        {
            throw new InvalidTransitionException(Status, to);
        }

        var fromStatus = Status;
        Status = to;
        UpdatedAt = now;

        return PaymentEvent.Create(
            paymentId: Id,
            fromStatus: fromStatus,
            toStatus: to,
            actor: actor,
            reason: reason,
            payload: payload,
            at: now);
    }

    private static IReadOnlyDictionary<string, string> ReasonPayload(string reason) =>
        new Dictionary<string, string>(1) { ["reason"] = reason };
}
