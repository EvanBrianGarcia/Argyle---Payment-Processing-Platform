namespace PaymentPlatform.Contracts.Payments;

/// Phase 2 supports only full refunds — the request carries a required
/// reason that lands in the payment_events.payload jsonb. Partial refunds
/// (an optional AmountMinor) are out of scope for Phase 2.
public sealed record RefundPaymentRequest(string Reason);
