using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.Application.Abstractions;

public interface IOutboxPublisher
{
    Task PublishSettlementAsync(SettlePayment message, CancellationToken cancellationToken);
}
