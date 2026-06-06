using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Infrastructure.Clock;
using PaymentPlatform.Infrastructure.Diagnostics;
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

        services.AddSingleton<PaymentVersionInterceptor>();

        services.AddDbContext<PaymentsDbContext>((sp, options) =>
            options
                .UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsAssembly(typeof(PaymentsDbContext).Assembly.FullName))
                .AddInterceptors(sp.GetRequiredService<PaymentVersionInterceptor>()));

        services.AddScoped<IPaymentsDbContext>(sp =>
            sp.GetRequiredService<PaymentsDbContext>());

        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddSingleton<IClock, SystemClock>();

        // PaymentsMeter wraps the process-global prometheus-net default
        // registry; singleton lifetime keeps every handler/observer pointing
        // at the same counters.
        services.AddSingleton<PaymentsMeter>();
        services.AddSingleton<IPaymentsMeter>(sp => sp.GetRequiredService<PaymentsMeter>());

        services
            .AddOptions<DiagnosticsOptions>()
            .Bind(configuration.GetSection(DiagnosticsOptions.SectionName));

        return services;
    }
}
