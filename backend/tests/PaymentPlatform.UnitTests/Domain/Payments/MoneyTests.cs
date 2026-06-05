using FluentAssertions;
using PaymentPlatform.Domain.Common;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.UnitTests.Domain.Payments;

public class MoneyTests
{
    [Fact]
    public void Constructor_WithPositiveAmountAndValidCurrency_SetsProperties()
    {
        var money = new Money(1000L, "USD");

        money.AmountMinor.Should().Be(1000L);
        money.Currency.Should().Be("USD");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Constructor_WithNonPositiveAmount_ThrowsInvalidAmount(long amount)
    {
        var act = () => new Money(amount, "USD");

        act.Should().Throw<DomainException>().Which.Code.Should().Be("invalid_amount");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("usd")]
    [InlineData("Usd")]
    [InlineData("U$D")]
    [InlineData("12A")]
    public void Constructor_WithInvalidCurrency_ThrowsInvalidCurrency(string currency)
    {
        var act = () => new Money(100L, currency);

        act.Should().Throw<DomainException>().Which.Code.Should().Be("invalid_currency");
    }

    [Fact]
    public void Constructor_WithNullCurrency_ThrowsInvalidCurrency()
    {
        var act = () => new Money(100L, null!);

        act.Should().Throw<DomainException>().Which.Code.Should().Be("invalid_currency");
    }

    [Fact]
    public void Equals_SameAmountAndCurrency_ReturnsTrue()
    {
        var a = new Money(500L, "USD");
        var b = new Money(500L, "USD");

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentAmount_ReturnsFalse()
    {
        var a = new Money(500L, "USD");
        var b = new Money(501L, "USD");

        a.Equals(b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentCurrency_ReturnsFalse()
    {
        var a = new Money(500L, "USD");
        var b = new Money(500L, "EUR");

        a.Equals(b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }
}
