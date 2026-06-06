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
using Prometheus;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    // Phase 4 Task 4 — the Worker is still a consumer-first process, but it
    // also exposes a tiny `/metrics` HTTP listener so Prometheus can scrape
    // queue metrics without going through the API. WebApplication.CreateBuilder
    // is fewer lines than running a second IHost and matches the master plan's
    // "lean" recommendation.
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((_, _, cfg) => cfg
        .Enrich.FromLogContext()
        .WriteTo.Console(new CompactJsonFormatter()));

    // Restrict Kestrel to the metrics port only — the worker is not a public
    // API surface and should never serve anything else. Default 9090 matches
    // the docker-compose port mapping; tests pass 0 to get an ephemeral port.
    var metricsPort = builder.Configuration.GetValue<int?>("Worker:MetricsPort") ?? 9090;
    if (metricsPort is < 1 or > 65535)
    {
        throw new InvalidOperationException(
            $"Worker:MetricsPort must be between 1 and 65535; got {metricsPort}.");
    }
    builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(metricsPort));

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

    var app = builder.Build();

    // Only `/metrics`. The worker is not an HTTP service; anything else on
    // this listener is a misuse and should 404.
    app.MapMetrics("/metrics");

    app.Run();
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
