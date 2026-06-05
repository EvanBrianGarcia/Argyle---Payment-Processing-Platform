using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Domain.Idempotency;

namespace PaymentPlatform.Infrastructure.Idempotency;

public sealed class IdempotencyStore : IIdempotencyStore
{
    private readonly IPaymentsDbContext _db;

    public IdempotencyStore(IPaymentsDbContext db)
    {
        _db = db;
    }

    public Task<IdempotencyKeyRecord?> FindAsync(
        string merchantId,
        string key,
        CancellationToken cancellationToken) =>
        _db.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(
                record => record.MerchantId == merchantId && record.Key == key,
                cancellationToken);

    public async Task SaveAsync(
        IdempotencyKeyRecord record,
        CancellationToken cancellationToken)
    {
        _db.IdempotencyKeys.Add(record);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
