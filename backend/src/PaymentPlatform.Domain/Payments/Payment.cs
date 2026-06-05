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
        DateTimeOffset createdAt)
    {
        Id = id;
        MerchantId = merchantId;
        Amount = amount;
        CardToken = cardToken;
        CustomerReference = customerReference;
        Metadata = metadata;
        Status = status;
        CreatedAt = createdAt;
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
            createdAt: now);
    }
}
