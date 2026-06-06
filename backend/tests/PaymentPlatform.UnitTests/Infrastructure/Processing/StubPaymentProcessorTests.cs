using FluentAssertions;
using Microsoft.Extensions.Options;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Infrastructure.Processing;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.UnitTests.Infrastructure.Processing;

public sealed class StubPaymentProcessorTests
{
    private static SettlePayment Message(string paymentId = "pay_test_001") => new(
        MessageId: "msg_" + Guid.NewGuid().ToString("N"),
        PaymentId: paymentId,
        MerchantId: "mrc_acme",
        AmountMinor: 12500L,
        Currency: "USD",
        CorrelationId: "trace-" + Guid.NewGuid().ToString("N"),
        Attempt: 1,
        EnqueuedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task AlwaysSucceed_ReturnsSuccess_ForEveryCall()
    {
        var processor = NewProcessor(new StubProcessorOptions { Mode = StubProcessorMode.AlwaysSucceed });
        var message = Message();

        var first = await processor.SettleAsync(message, CancellationToken.None);
        var second = await processor.SettleAsync(message, CancellationToken.None);
        var third = await processor.SettleAsync(message, CancellationToken.None);

        first.Should().BeOfType<ProcessorResult.Success>();
        second.Should().BeOfType<ProcessorResult.Success>();
        third.Should().BeOfType<ProcessorResult.Success>();
    }

    [Fact]
    public async Task AlwaysSucceed_RecordsCallCount_PerPaymentId()
    {
        var processor = NewProcessor(new StubProcessorOptions { Mode = StubProcessorMode.AlwaysSucceed });
        var a = Message(paymentId: "pay_aaa");
        var b = Message(paymentId: "pay_bbb");

        await processor.SettleAsync(a, CancellationToken.None);
        await processor.SettleAsync(a, CancellationToken.None);
        await processor.SettleAsync(b, CancellationToken.None);

        processor.CallCountFor("pay_aaa").Should().Be(2);
        processor.CallCountFor("pay_bbb").Should().Be(1);
        processor.CallCountFor("pay_ccc").Should().Be(0);
    }

    [Fact]
    public async Task FailNTimesThenSucceed_ReturnsTransient_ForFirstNCalls_ThenSuccess()
    {
        var processor = NewProcessor(new StubProcessorOptions
        {
            Mode = StubProcessorMode.FailNTimesThenSucceed,
            FailureCount = 2,
        });
        var message = Message();

        var first = await processor.SettleAsync(message, CancellationToken.None);
        var second = await processor.SettleAsync(message, CancellationToken.None);
        var third = await processor.SettleAsync(message, CancellationToken.None);

        first.Should().BeOfType<ProcessorResult.TransientFailure>();
        second.Should().BeOfType<ProcessorResult.TransientFailure>();
        third.Should().BeOfType<ProcessorResult.Success>();
    }

    [Fact]
    public async Task FailNTimesThenSucceed_TracksFailureCount_IndependentlyPerPaymentId()
    {
        var processor = NewProcessor(new StubProcessorOptions
        {
            Mode = StubProcessorMode.FailNTimesThenSucceed,
            FailureCount = 1,
        });
        var a = Message(paymentId: "pay_aaa");
        var b = Message(paymentId: "pay_bbb");

        var aFirst = await processor.SettleAsync(a, CancellationToken.None);
        var bFirst = await processor.SettleAsync(b, CancellationToken.None);
        var aSecond = await processor.SettleAsync(a, CancellationToken.None);
        var bSecond = await processor.SettleAsync(b, CancellationToken.None);

        aFirst.Should().BeOfType<ProcessorResult.TransientFailure>();
        bFirst.Should().BeOfType<ProcessorResult.TransientFailure>();
        aSecond.Should().BeOfType<ProcessorResult.Success>();
        bSecond.Should().BeOfType<ProcessorResult.Success>();
    }

    [Fact]
    public async Task ResetCounts_ClearsFailureBookkeeping()
    {
        var processor = NewProcessor(new StubProcessorOptions
        {
            Mode = StubProcessorMode.FailNTimesThenSucceed,
            FailureCount = 2,
        });
        var message = Message();

        await processor.SettleAsync(message, CancellationToken.None);
        await processor.SettleAsync(message, CancellationToken.None);
        processor.ResetCounts();
        var afterReset = await processor.SettleAsync(message, CancellationToken.None);

        afterReset.Should().BeOfType<ProcessorResult.TransientFailure>();
        processor.CallCountFor(message.PaymentId).Should().Be(1);
    }

    [Fact]
    public async Task AlwaysFailPermanent_ReturnsPermanent_ForEveryCall_AndIsDistinguishableFromTransient()
    {
        var processor = NewProcessor(new StubProcessorOptions { Mode = StubProcessorMode.AlwaysFailPermanent });
        var message = Message();

        var first = await processor.SettleAsync(message, CancellationToken.None);
        var second = await processor.SettleAsync(message, CancellationToken.None);

        first.Should().BeOfType<ProcessorResult.PermanentFailure>();
        second.Should().BeOfType<ProcessorResult.PermanentFailure>();
        first.Should().NotBeOfType<ProcessorResult.TransientFailure>();
    }

    [Fact]
    public async Task PerPaymentOverride_WinsOverGlobalMode()
    {
        var processor = NewProcessor(new StubProcessorOptions
        {
            Mode = StubProcessorMode.AlwaysSucceed,
            PerPaymentOverrides = new Dictionary<string, StubProcessorMode>
            {
                ["pay_failer"] = StubProcessorMode.AlwaysFailPermanent,
            },
        });

        var globalDefault = await processor.SettleAsync(Message(paymentId: "pay_normal"), CancellationToken.None);
        var overridden = await processor.SettleAsync(Message(paymentId: "pay_failer"), CancellationToken.None);

        globalDefault.Should().BeOfType<ProcessorResult.Success>();
        overridden.Should().BeOfType<ProcessorResult.PermanentFailure>();
    }

    [Fact]
    public async Task Success_CarriesExternalReference()
    {
        var processor = NewProcessor(new StubProcessorOptions { Mode = StubProcessorMode.AlwaysSucceed });

        var result = await processor.SettleAsync(Message(), CancellationToken.None);

        var success = result.Should().BeOfType<ProcessorResult.Success>().Subject;
        success.ExternalReference.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TransientFailure_CarriesReason()
    {
        var processor = NewProcessor(new StubProcessorOptions
        {
            Mode = StubProcessorMode.FailNTimesThenSucceed,
            FailureCount = 1,
        });

        var result = await processor.SettleAsync(Message(), CancellationToken.None);

        var transient = result.Should().BeOfType<ProcessorResult.TransientFailure>().Subject;
        transient.Reason.Should().NotBeNullOrWhiteSpace();
    }

    private static StubPaymentProcessor NewProcessor(StubProcessorOptions options) =>
        new(Options.Create(options));
}
