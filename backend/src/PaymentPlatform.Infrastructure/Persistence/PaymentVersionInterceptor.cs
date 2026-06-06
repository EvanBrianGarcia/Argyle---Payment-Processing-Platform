using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.Infrastructure.Persistence;

/// Bumps the `version` shadow property on every modified Payment so that
/// the EF Core optimistic-concurrency check actually catches conflicts.
/// Without this, IsConcurrencyToken() on `version` only generates a WHERE
/// clause but never changes the stored value, so two concurrent UPDATE
/// statements both match `WHERE version=N` and both succeed.
public sealed class PaymentVersionInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        BumpVersions(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        BumpVersions(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private static void BumpVersions(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<Payment>())
        {
            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            var versionProperty = entry.Property<int>("Version");
            versionProperty.CurrentValue = versionProperty.CurrentValue + 1;
        }
    }
}
