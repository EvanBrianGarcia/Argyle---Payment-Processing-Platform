using FluentAssertions;
using PaymentPlatform.Domain.Common;

namespace PaymentPlatform.UnitTests.Domain.Common;

public class IdGeneratorTests
{
    [Fact]
    public void NewPaymentId_StartsWithPaymentPrefix()
    {
        var id = IdGenerator.NewPaymentId();

        id.Should().StartWith("pay_");
        id.Length.Should().BeGreaterThan("pay_".Length);
    }

    [Fact]
    public void NewMerchantId_StartsWithMerchantPrefix()
    {
        var id = IdGenerator.NewMerchantId();

        id.Should().StartWith("mrc_");
        id.Length.Should().BeGreaterThan("mrc_".Length);
    }

    [Fact]
    public void NewEventId_StartsWithEventPrefix()
    {
        var id = IdGenerator.NewEventId();

        id.Should().StartWith("evt_");
        id.Length.Should().BeGreaterThan("evt_".Length);
    }

    [Fact]
    public void NewPaymentId_OneHundredCalls_ProduceUniqueIds()
    {
        var ids = Enumerable.Range(0, 100)
            .Select(_ => IdGenerator.NewPaymentId())
            .ToHashSet();

        ids.Should().HaveCount(100);
    }

    [Fact]
    public void NewMerchantId_OneHundredCalls_ProduceUniqueIds()
    {
        var ids = Enumerable.Range(0, 100)
            .Select(_ => IdGenerator.NewMerchantId())
            .ToHashSet();

        ids.Should().HaveCount(100);
    }

    [Fact]
    public void NewEventId_OneHundredCalls_ProduceUniqueIds()
    {
        var ids = Enumerable.Range(0, 100)
            .Select(_ => IdGenerator.NewEventId())
            .ToHashSet();

        ids.Should().HaveCount(100);
    }
}
