using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.Domain.Common;

public sealed class InvalidTransitionException : DomainException
{
    public PaymentStatus From { get; }
    public PaymentStatus To { get; }

    public InvalidTransitionException(PaymentStatus from, PaymentStatus to)
        : base(
            code: "invalid_state_transition",
            message: $"Payment cannot transition from {from} to {to}.")
    {
        From = from;
        To = to;
    }
}
