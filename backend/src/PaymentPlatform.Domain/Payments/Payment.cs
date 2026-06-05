using PaymentPlatform.Domain.Common;

namespace PaymentPlatform.Domain.Payments;

public sealed class Payment
{
    public string Id { get; }
    public string MerchantId { get; }
    public Money Amount { get; }
    public string CardToken { get; }
    public string? CustomerReference { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
    public PaymentStatus Status { get; }
    public DateTimeOffset CreatedAt { get; }

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
