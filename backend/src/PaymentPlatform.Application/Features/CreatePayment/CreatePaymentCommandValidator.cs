using FluentValidation;

namespace PaymentPlatform.Application.Features.CreatePayment;

public sealed class CreatePaymentCommandValidator : AbstractValidator<CreatePaymentCommand>
{
    public CreatePaymentCommandValidator()
    {
        RuleFor(c => c.IdempotencyKey)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithMessage("Idempotency key is required.");

        RuleFor(c => c.AmountMinor)
            .GreaterThan(0)
            .WithMessage("Amount must be positive.");

        RuleFor(c => c.Currency)
            .NotNull()
            .Matches("^[A-Z]{3}$")
            .WithMessage("Currency must be exactly 3 uppercase ASCII letters.");

        RuleFor(c => c.CardToken)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithMessage("Card token is required.");
    }
}
