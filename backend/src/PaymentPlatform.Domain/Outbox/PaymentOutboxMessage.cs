using PaymentPlatform.Domain.Common;

namespace PaymentPlatform.Domain.Outbox;

public sealed class PaymentOutboxMessage
{
    public long Id { get; private set; }
    public string AggregateId { get; private set; } = default!;
    public string MessageType { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public string CorrelationId { get; private set; } = default!;
    public string? Traceparent { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }

    private PaymentOutboxMessage()
    {
    }

    private PaymentOutboxMessage(
        string aggregateId,
        string messageType,
        string payload,
        string correlationId,
        string? traceparent,
        DateTimeOffset createdAt)
    {
        AggregateId = aggregateId;
        MessageType = messageType;
        Payload = payload;
        CorrelationId = correlationId;
        Traceparent = traceparent;
        CreatedAt = createdAt;
        DispatchedAt = null;
    }

    public static PaymentOutboxMessage Create(
        string aggregateId,
        string messageType,
        string payload,
        string correlationId,
        DateTimeOffset createdAt,
        string? traceparent = null)
    {
        if (string.IsNullOrWhiteSpace(aggregateId))
        {
            throw new DomainException("invalid_outbox_aggregate_id", "Outbox aggregate id must be non-blank.");
        }

        if (string.IsNullOrWhiteSpace(messageType))
        {
            throw new DomainException("invalid_outbox_message_type", "Outbox message type must be non-blank.");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new DomainException("invalid_outbox_payload", "Outbox payload must be non-blank.");
        }

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new DomainException("invalid_outbox_correlation_id", "Outbox correlation id must be non-blank.");
        }

        return new PaymentOutboxMessage(
            aggregateId: aggregateId,
            messageType: messageType,
            payload: payload,
            correlationId: correlationId,
            traceparent: string.IsNullOrWhiteSpace(traceparent) ? null : traceparent,
            createdAt: createdAt);
    }
}
