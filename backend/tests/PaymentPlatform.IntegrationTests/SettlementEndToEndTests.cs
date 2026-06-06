using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.IntegrationTests.Fixtures;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.IntegrationTests;

/// Phase 3 Task 3 wiring test. Asserts that publishing a SettlePayment via
/// the API's MassTransit publisher actually reaches a consumer registered
/// in a sibling Worker host through the real RabbitMQ broker.
public sealed class SettlementEndToEndTests : IAsyncLifetime
{
    private readonly MessagingFixture _fixture = new();
    private MessagingApiFactory _apiFactory = default!;
    private WorkerTestHost _worker = default!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await _fixture.Postgres.ResetDatabaseAsync();

        _apiFactory = new MessagingApiFactory(_fixture);
        _ = _apiFactory.CreateClient();

        _worker = await WorkerTestHost.StartAsync(_fixture);
    }

    public async Task DisposeAsync()
    {
        await _worker.DisposeAsync();
        _apiFactory.Dispose();
        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task PublishingSettlePayment_ReachesWorkerConsumer_WithinFiveSeconds()
    {
        using var scope = _apiFactory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var message = new SettlePayment(
            MessageId: "msg_" + Guid.NewGuid(),
            PaymentId: "pay_test_" + Guid.NewGuid(),
            MerchantId: "mrc_acme",
            AmountMinor: 12500,
            Currency: "USD",
            CorrelationId: "trace-" + Guid.NewGuid(),
            Attempt: 1,
            EnqueuedAt: DateTimeOffset.UtcNow);

        await publisher.Publish(message);

        var received = await _worker.Sink.WaitForNextAsync(TimeSpan.FromSeconds(10));

        received.Should().NotBeNull("the consumer should observe the message through the real broker");
        received!.PaymentId.Should().Be(message.PaymentId);
        received.CorrelationId.Should().Be(message.CorrelationId);
    }
}
