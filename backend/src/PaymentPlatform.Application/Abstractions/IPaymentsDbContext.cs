using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Domain.Idempotency;
using PaymentPlatform.Domain.Merchants;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.Application.Abstractions;

public interface IPaymentsDbContext
{
    DbSet<Payment> Payments { get; }
    DbSet<Merchant> Merchants { get; }
    DbSet<IdempotencyKeyRecord> IdempotencyKeys { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
