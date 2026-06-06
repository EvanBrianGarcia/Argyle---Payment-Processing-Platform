using FluentValidation;

namespace PaymentPlatform.Application.Features.CapturePayment;

public sealed class CapturePaymentCommandValidator : AbstractValidator<CapturePaymentCommand>
{
    public CapturePaymentCommandValidator()
    {
        RuleFor(c => c.IdempotencyKey)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithMessage("Idempotency key is required.");

        RuleFor(c => c.PaymentId)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithMessage("Payment id is required.");

        When(c => c.AmountMinor.HasValue, () =>
        {
            RuleFor(c => c.AmountMinor!.Value)
                .GreaterThan(0)
                .WithMessage("Amount, when provided, must be positive.");
        });
    }
}
