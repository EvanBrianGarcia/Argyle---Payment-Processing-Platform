namespace PaymentPlatform.Contracts.Payments;

/// Phase 2 ignores partial captures (master plan §17). The optional
/// AmountMinor is accepted for forward compat and validated as positive
/// when present, but the handler does NOT enforce that it equals the
/// original authorized amount — that lands when partial captures arrive.
public sealed record CapturePaymentRequest(long? AmountMinor);
