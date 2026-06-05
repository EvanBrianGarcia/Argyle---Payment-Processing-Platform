using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Infrastructure.Clock;
using PaymentPlatform.Infrastructure.Idempotency;
using PaymentPlatform.Infrastructure.Persistence;

namespace PaymentPlatform.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Payments")
            ?? throw new InvalidOperationException(
                "Connection string 'Payments' is not configured.");

        services.AddDbContext<PaymentsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(PaymentsDbContext).Assembly.FullName)));

        services.AddScoped<IPaymentsDbContext>(sp =>
            sp.GetRequiredService<PaymentsDbContext>());

        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }
}
