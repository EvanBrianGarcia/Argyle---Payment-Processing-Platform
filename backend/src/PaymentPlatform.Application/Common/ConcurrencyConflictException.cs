namespace PaymentPlatform.Application.Common;

public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
        : base("The payment was modified concurrently. Retry the request.")
    {
    }
}
