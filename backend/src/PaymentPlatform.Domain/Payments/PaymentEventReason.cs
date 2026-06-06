namespace PaymentPlatform.Domain.Payments;

public static class PaymentEventReason
{
    public const string Created = "created";
    public const string AuthOk = "auth_ok";
    public const string AuthFailed = "auth_failed";
    public const string Captured = "captured";
    public const string Settled = "settled";
    public const string Refunded = "refunded";
    public const string Failed = "failed";
}
