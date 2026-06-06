namespace PaymentPlatform.Contracts.Payments;

/// List endpoint payload. Each item's `Events` array is intentionally empty —
/// the list endpoint omits the event timeline to avoid N+1; clients fetch
/// GET /v1/payments/{id} for the full per-payment history.
public sealed record PaymentListResponse(
    IReadOnlyList<PaymentResponse> Data,
    string? NextCursor);
