using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PaymentPlatform.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=localhost;Port=5432;Database=payments;Username=postgres;Password=postgres";

    public PaymentsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(DesignTimeConnectionString, b =>
                b.MigrationsAssembly(typeof(PaymentsDbContext).Assembly.FullName))
            .Options;

        return new PaymentsDbContext(options);
    }
}
