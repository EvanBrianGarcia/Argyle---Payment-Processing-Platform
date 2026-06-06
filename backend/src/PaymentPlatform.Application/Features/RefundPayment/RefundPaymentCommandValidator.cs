using FluentValidation;

namespace PaymentPlatform.Application.Features.RefundPayment;

public sealed class RefundPaymentCommandValidator : AbstractValidator<RefundPaymentCommand>
{
    public RefundPaymentCommandValidator()
    {
        RuleFor(c => c.IdempotencyKey)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithMessage("Idempotency key is required.");

        RuleFor(c => c.PaymentId)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithMessage("Payment id is required.");

        RuleFor(c => c.Reason)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithMessage("Reason is required.");
    }
}
