using FluentAssertions;
using FluentValidation;
using PaymentPlatform.Application.Features.CreatePayment;

namespace PaymentPlatform.UnitTests.Features.CreatePayment;

public sealed class CreatePaymentCommandValidatorTests
{
    private static readonly CreatePaymentCommandValidator Validator = new();

    [Fact]
    public void Valid_command_passes_validation()
    {
        var command = ValidCommand();

        var result = Validator.Validate(command);

        result.IsValid.Should().BeTrue(
            "errors: {0}",
            string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_idempotency_key_fails(string key)
    {
        var command = ValidCommand() with { IdempotencyKey = key };

        var result = Validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreatePaymentCommand.IdempotencyKey));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void Non_positive_amount_fails(long amount)
    {
        var command = ValidCommand() with { AmountMinor = amount };

        var result = Validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreatePaymentCommand.AmountMinor));
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("usd")]
    [InlineData("Usd")]
    [InlineData("U5D")]
    public void Invalid_currency_fails(string currency)
    {
        var command = ValidCommand() with { Currency = currency };

        var result = Validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreatePaymentCommand.Currency));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_card_token_fails(string token)
    {
        var command = ValidCommand() with { CardToken = token };

        var result = Validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreatePaymentCommand.CardToken));
    }

    [Fact]
    public void Null_customer_reference_is_allowed()
    {
        var command = ValidCommand() with { CustomerReference = null };

        var result = Validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Null_metadata_is_allowed()
    {
        var command = ValidCommand() with { Metadata = null };

        var result = Validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    private static CreatePaymentCommand ValidCommand() => new(
        IdempotencyKey: "key-1",
        AmountMinor: 1000,
        Currency: "USD",
        CardToken: "tok_abc",
        CustomerReference: null,
        Metadata: null);
}
