using PaymentPlatform.Application.Abstractions;

namespace PaymentPlatform.Api.Auth;

public sealed class CurrentMerchant : ICurrentMerchant
{
    private string? _merchantId;

    public string MerchantId =>
        _merchantId ?? throw new InvalidOperationException(
            "Current merchant has not been set. Auth middleware must run before reaching the handler.");

    internal void Set(string merchantId)
    {
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            throw new ArgumentException("Merchant id must be non-blank.", nameof(merchantId));
        }
        _merchantId = merchantId;
    }
}
