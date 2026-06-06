using MassTransit;
using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Infrastructure.Clock;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.Infrastructure.Processing;
using PaymentPlatform.Messaging.Settlement;
using PaymentPlatform.Worker.Consumers;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((_, cfg) => cfg
        .Enrich.FromLogContext()
        .WriteTo.Console(new CompactJsonFormatter()));

    var connectionString = builder.Configuration.GetConnectionString("Payments")
        ?? throw new InvalidOperationException("ConnectionStrings:Payments is not configured.");

    builder.Services.AddDbContext<PaymentsDbContext>(options =>
        options.UseNpgsql(connectionString));
    builder.Services.AddScoped<IPaymentsDbContext>(sp => sp.GetRequiredService<PaymentsDbContext>());

    builder.Services.AddSingleton<IClock, SystemClock>();

    builder.Services.Configure<StubProcessorOptions>(
        builder.Configuration.GetSection(StubProcessorOptions.SectionName));
    builder.Services.AddSingleton<StubPaymentProcessor>();
    builder.Services.AddSingleton<IPaymentProcessor>(sp => sp.GetRequiredService<StubPaymentProcessor>());

    builder.Services.AddMassTransit(cfg =>
    {
        cfg.AddConsumer<SettlePaymentConsumer>();

        cfg.UsingRabbitMq((context, rmq) =>
        {
            var section = builder.Configuration.GetSection("RabbitMq");
            var port = ushort.TryParse(section["Port"], out var parsed) ? parsed : (ushort)5672;

            rmq.Host(
                section["Host"] ?? "localhost",
                port,
                section["VirtualHost"] ?? "/",
                h =>
                {
                    h.Username(section["Username"] ?? "guest");
                    h.Password(section["Password"] ?? "guest");
                });

            rmq.ReceiveEndpoint(SettlementQueues.Queue, e =>
            {
                e.ConfigureConsumer<SettlePaymentConsumer>(context);
            });
        });
    });

    var host = builder.Build();
    host.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Worker terminated unexpectedly.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

namespace PaymentPlatform.Worker
{
    public partial class Program { }
}
