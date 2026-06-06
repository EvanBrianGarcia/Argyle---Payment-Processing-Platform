namespace PaymentPlatform.Contracts.Payments;

public sealed record PaymentResponse(
    string Id,
    long AmountMinor,
    string Currency,
    string Status,
    string? CustomerReference,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PaymentEventDto> Events);
