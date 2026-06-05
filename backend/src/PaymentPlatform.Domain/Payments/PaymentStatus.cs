namespace PaymentPlatform.Domain.Payments;

public enum PaymentStatus
{
    Pending = 0,
    Authorized = 1,
    Captured = 2,
    Settled = 3,
    Failed = 4,
    Refunded = 5,
}
