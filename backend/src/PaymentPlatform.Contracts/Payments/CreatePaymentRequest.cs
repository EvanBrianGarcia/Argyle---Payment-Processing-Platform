namespace PaymentPlatform.Contracts.Payments;

public sealed record CreatePaymentRequest(
    long AmountMinor,
    string Currency,
    string CardToken,
    string? CustomerReference,
    IReadOnlyDictionary<string, string>? Metadata);
