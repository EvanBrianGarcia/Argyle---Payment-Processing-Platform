namespace PaymentPlatform.Application.Common;

public static class IdempotencyOperations
{
    public const string CreatePayment = "create_payment";
    public const string CapturePayment = "capture_payment";
    public const string RefundPayment = "refund_payment";
}
