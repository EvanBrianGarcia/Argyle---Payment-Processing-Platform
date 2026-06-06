using PaymentPlatform.Domain.Idempotency;

namespace PaymentPlatform.Application.Abstractions;

public interface IIdempotencyStore
{
    Task<IdempotencyKeyRecord?> FindAsync(
        string merchantId,
        string operation,
        string key,
        CancellationToken cancellationToken);

    Task SaveAsync(
        IdempotencyKeyRecord record,
        CancellationToken cancellationToken);
}
