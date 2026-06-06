using MassTransit;
using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Infrastructure.Clock;
using PaymentPlatform.Infrastructure.Diagnostics;
using PaymentPlatform.Infrastructure.Messaging;
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

    builder.Services.AddPaymentsTelemetry(builder.Configuration, "PaymentPlatform.Worker");

    builder.Services.AddSingleton<PaymentsMeter>();
    builder.Services.AddSingleton<IPaymentsMeter>(sp => sp.GetRequiredService<PaymentsMeter>());
    builder.Services
        .AddOptions<DiagnosticsOptions>()
        .Bind(builder.Configuration.GetSection(DiagnosticsOptions.SectionName));
    builder.Services.AddHostedService<PaymentStatusGaugeUpdater>();

    var retryOptions = builder.Configuration
        .GetSection(WorkerRetryOptions.SectionName)
        .Get<WorkerRetryOptions>() ?? new WorkerRetryOptions();

    builder.Services.AddMassTransit(cfg =>
    {
        cfg.AddConsumer<SettlePaymentConsumer>();
        cfg.AddConsumeObserver<MetricsConsumerObserver>();

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
                e.ConfigureSettlementEndpoint(context, retryOptions);
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
