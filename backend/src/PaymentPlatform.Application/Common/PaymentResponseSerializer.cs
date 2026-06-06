using System.Text.Json;
using PaymentPlatform.Contracts.Payments;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.Application.Common;

/// Centralizes PaymentResponse serialization for idempotency caches and
/// the per-handler aggregate→DTO projection. Keeping the JSON options in
/// one place ensures cached responses round-trip byte-identically across
/// create / capture / refund handlers.
public static class PaymentResponseSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string Serialize(PaymentResponse response) =>
        JsonSerializer.Serialize(response, Options);

    public static PaymentResponse Deserialize(string body) =>
        JsonSerializer.Deserialize<PaymentResponse>(body, Options)
            ?? throw new InvalidOperationException(
                "Cached idempotency response body could not be deserialized.");

    public static PaymentResponse ToResponse(
        Payment payment,
        IEnumerable<PaymentEvent> events) => new(
            Id: payment.Id,
            AmountMinor: payment.Amount.AmountMinor,
            Currency: payment.Amount.Currency,
            Status: payment.Status.ToString(),
            CustomerReference: payment.CustomerReference,
            Metadata: payment.Metadata,
            CreatedAt: payment.CreatedAt,
            UpdatedAt: payment.UpdatedAt,
            Events: events.Select(ToEventDto).ToList());

    private static PaymentEventDto ToEventDto(PaymentEvent evt) => new(
        Id: evt.Id,
        FromStatus: evt.FromStatus?.ToString(),
        ToStatus: evt.ToStatus.ToString(),
        Actor: evt.Actor,
        Reason: evt.Reason,
        Payload: evt.Payload,
        At: evt.At);
}
