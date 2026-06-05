using FluentAssertions;
using PaymentPlatform.Domain.Common;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.UnitTests.Domain.Payments;

public class PaymentTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 5, 14, 30, 0, TimeSpan.Zero);

    private static Money DefaultAmount() => new(1000L, "USD");

    [Fact]
    public void Create_HappyPath_ReturnsPendingPayment()
    {
        var amount = DefaultAmount();

        var payment = Payment.Create(
            merchantId: "mrc_123",
            amount: amount,
            cardToken: "tok_visa_4242",
            customerReference: "order-abc",
            metadata: new Dictionary<string, string> { ["sku"] = "widget" },
            now: Now);

        payment.Status.Should().Be(PaymentStatus.Pending);
        payment.MerchantId.Should().Be("mrc_123");
        payment.Amount.Should().Be(amount);
        payment.CardToken.Should().Be("tok_visa_4242");
        payment.CustomerReference.Should().Be("order-abc");
        payment.Metadata.Should().ContainKey("sku").WhoseValue.Should().Be("widget");
        payment.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void Create_IdHasPaymentPrefix()
    {
        var payment = Payment.Create(
            merchantId: "mrc_123",
            amount: DefaultAmount(),
            cardToken: "tok_x",
            customerReference: null,
            metadata: null,
            now: Now);

        payment.Id.Should().StartWith("pay_");
        payment.Id.Length.Should().BeGreaterThan("pay_".Length);
    }

    [Fact]
    public void Create_TwoPayments_HaveUniqueIds()
    {
        var first = Payment.Create("mrc_1", DefaultAmount(), "tok_a", null, null, Now);
        var second = Payment.Create("mrc_1", DefaultAmount(), "tok_a", null, null, Now);

        first.Id.Should().NotBe(second.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankMerchantId_ThrowsInvalidMerchant(string? merchantId)
    {
        var act = () => Payment.Create(
            merchantId!,
            DefaultAmount(),
            "tok_x",
            null,
            null,
            Now);

        act.Should().Throw<DomainException>().Which.Code.Should().Be("invalid_merchant");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankCardToken_ThrowsInvalidCardToken(string? cardToken)
    {
        var act = () => Payment.Create(
            "mrc_123",
            DefaultAmount(),
            cardToken!,
            null,
            null,
            Now);

        act.Should().Throw<DomainException>().Which.Code.Should().Be("invalid_card_token");
    }

    [Fact]
    public void Create_NullMetadata_DefaultsToEmpty()
    {
        var payment = Payment.Create(
            "mrc_123",
            DefaultAmount(),
            "tok_x",
            customerReference: null,
            metadata: null,
            now: Now);

        payment.Metadata.Should().NotBeNull();
        payment.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void Create_NullCustomerReference_IsAllowed()
    {
        var payment = Payment.Create(
            "mrc_123",
            DefaultAmount(),
            "tok_x",
            customerReference: null,
            metadata: null,
            now: Now);

        payment.CustomerReference.Should().BeNull();
    }
}
