namespace PaymentPlatform.Application.Common;

public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException()
        : base("Idempotency key was used with a different request body.")
    {
    }
}
