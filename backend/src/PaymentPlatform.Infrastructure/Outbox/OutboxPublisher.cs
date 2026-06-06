using MassTransit;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.Infrastructure.Outbox;

/// Thin adapter from the Application-layer abstraction to MassTransit's
/// IPublishEndpoint. Lives in Infrastructure so the Application project
/// stays transport-agnostic.
public sealed class OutboxPublisher : IOutboxPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;

    public OutboxPublisher(IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public Task PublishSettlementAsync(SettlePayment message, CancellationToken cancellationToken) =>
        _publishEndpoint.Publish(
            message,
            context =>
            {
                context.MessageId = ParseMessageId(message.MessageId);
                context.CorrelationId = ParseCorrelationId(message.CorrelationId);
            },
            cancellationToken);

    private static Guid? ParseMessageId(string messageId) =>
        Guid.TryParse(messageId.StartsWith("msg_", StringComparison.Ordinal)
            ? messageId[4..]
            : messageId, out var parsed) ? parsed : null;

    private static Guid? ParseCorrelationId(string correlationId) =>
        Guid.TryParse(correlationId, out var parsed) ? parsed : null;
}
