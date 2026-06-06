namespace PaymentPlatform.Contracts.Payments;

public sealed record PaymentEventDto(
    string Id,
    string? FromStatus,
    string ToStatus,
    string Actor,
    string Reason,
    IReadOnlyDictionary<string, string> Payload,
    DateTimeOffset At);
