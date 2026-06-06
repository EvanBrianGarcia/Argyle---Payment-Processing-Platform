using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Infrastructure.Outbox;

namespace PaymentPlatform.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// Registers the MassTransit publisher used by the API host (and by the
    /// outbox dispatcher). No consumers — the Worker host owns those.
    public static IServiceCollection AddPaymentMessagingPublisher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = RabbitMqOptions.From(configuration);

        services.AddMassTransit(cfg =>
        {
            cfg.UsingRabbitMq((_, rmq) =>
            {
                rmq.Host(
                    options.Host,
                    options.Port,
                    options.VirtualHost,
                    h =>
                    {
                        h.Username(options.Username);
                        h.Password(options.Password);
                    });
            });
        });

        services.AddScoped<IOutboxPublisher, OutboxPublisher>();

        return services;
    }

    private sealed record RabbitMqOptions(
        string Host,
        ushort Port,
        string VirtualHost,
        string Username,
        string Password)
    {
        public static RabbitMqOptions From(IConfiguration configuration)
        {
            var section = configuration.GetSection("RabbitMq");
            return new RabbitMqOptions(
                Host: section["Host"] ?? "localhost",
                Port: ParsePort(section["Port"]),
                VirtualHost: section["VirtualHost"] ?? "/",
                Username: section["Username"] ?? "guest",
                Password: section["Password"] ?? "guest");
        }

        private static ushort ParsePort(string? raw) =>
            ushort.TryParse(raw, out var parsed) ? parsed : (ushort)5672;
    }
}
