using FluentValidation;
using PaymentPlatform.Api.Auth;
using PaymentPlatform.Api.Serialization;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Features.CreatePayment;

namespace PaymentPlatform.Api;

public static class DependencyInjection
{
    public static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddScoped<CurrentMerchant>();
        services.AddScoped<ICurrentMerchant>(sp => sp.GetRequiredService<CurrentMerchant>());

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(CreatePaymentCommand).Assembly));

        services.AddValidatorsFromAssemblyContaining<CreatePaymentCommandValidator>();

        services.ConfigureHttpJsonOptions(options =>
            JsonOptions.Configure(options.SerializerOptions));

        return services;
    }
}
