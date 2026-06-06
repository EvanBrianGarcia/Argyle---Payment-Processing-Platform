using System.Net;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Infrastructure.Diagnostics;
using PaymentPlatform.Infrastructure.Messaging;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.Messaging.Settlement;
using PaymentPlatform.Worker.Consumers;
using Prometheus;

namespace PaymentPlatform.IntegrationTests.Fixtures;

/// In-process IHost that mirrors the Worker's production MassTransit wiring
/// against the real RabbitMQ + Postgres fixtures. Substitutes
/// ControllableProcessor + MutableTestClock for the production singletons so
/// retry/DLQ tests can drive deterministic processor outcomes and shortens
/// the retry intervals to milliseconds so the suite finishes in seconds.
///
/// Phase 4 Task 4 adds a `/metrics` listener to the production Worker. The
/// fixture mirrors that capability via a SEPARATE WebApplication that runs
/// alongside the IHost and shares the process-global Metrics.DefaultRegistry
/// (the same registry the IHost's PaymentsMeter writes to). Keeping the IHost
/// shape identical to its proven pre-Task-4 form preserves the deterministic
/// MassTransit shutdown order — switching the main host to WebApplication
/// flaked HappyPath/DLQ/Retry tests because Kestrel's slower disposal raced
/// RabbitMQ consumer unbind on a broker shared via MessagingFixture.
internal sealed class SettlementWorkerHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly WebApplication _metricsApp;

    public ControllableProcessor Processor { get; }
    public MutableTestClock Clock { get; }
    public SettlementFaultSink Faults { get; }
    public int MetricsPort { get; }

    private SettlementWorkerHost(
        IHost host,
        WebApplication metricsApp,
        ControllableProcessor processor,
        MutableTestClock clock,
        SettlementFaultSink faults,
        int metricsPort)
    {
        _host = host;
        _metricsApp = metricsApp;
        Processor = processor;
        Clock = clock;
        Faults = faults;
        MetricsPort = metricsPort;
    }

    public IServiceProvider Services => _host.Services;

    public static async Task<SettlementWorkerHost> StartAsync(
        MessagingFixture fixture,
        WorkerRetryOptions retryOptions)
    {
        var processor = new ControllableProcessor();
        var clock = new MutableTestClock();
        var faults = new SettlementFaultSink();

        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<IPaymentProcessor>(processor);
        builder.Services.AddSingleton(processor);
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton(faults);

        builder.Services.AddDbContext<PaymentsDbContext>(options =>
            options.UseNpgsql(fixture.Postgres.ConnectionString));
        builder.Services.AddScoped<IPaymentsDbContext>(sp =>
            sp.GetRequiredService<PaymentsDbContext>());

        // Register the same meter/observer wiring the production Worker uses
        // so MetricsEndpointTests' worker-driven assertions observe the
        // queue counters in the process-shared default registry.
        builder.Services.AddSingleton<PaymentsMeter>();
        builder.Services.AddSingleton<IPaymentsMeter>(sp => sp.GetRequiredService<PaymentsMeter>());

        builder.Services.AddMassTransit(cfg =>
        {
            cfg.AddConsumer<SettlePaymentConsumer>();
            cfg.AddConsumeObserver<MetricsConsumerObserver>();

            cfg.UsingRabbitMq((context, rmq) =>
            {
                rmq.Host(
                    fixture.RabbitMq.Host,
                    (ushort)fixture.RabbitMq.Port,
                    "/",
                    h =>
                    {
                        h.Username(fixture.RabbitMq.Username);
                        h.Password(fixture.RabbitMq.Password);
                    });

                rmq.ReceiveEndpoint(SettlementQueues.Queue, e =>
                {
                    e.ConfigureSettlementEndpoint(context, retryOptions);
                });
            });
        });

        var host = builder.Build();
        await host.StartAsync();

        // Attach the consume-fault observer at runtime so it fires for every
        // SettlePaymentConsumer fault — retry-exhaustion or Ignore<> match.
        var bus = host.Services.GetRequiredService<IBusControl>();
        bus.ConnectConsumeObserver(new SettlementConsumeFaultObserver(faults));

        var metricsApp = await StartMetricsListenerAsync();
        var metricsPort = ResolveBoundPort(metricsApp);

        return new SettlementWorkerHost(host, metricsApp, processor, clock, faults, metricsPort);
    }

    public async ValueTask DisposeAsync()
    {
        // Tear down the metrics listener first — it has no consumers, so its
        // disposal is fast and non-racy. The IHost's MassTransit shutdown is
        // the time-sensitive step; doing it last keeps the consumer-unbind
        // path on the proven pre-Task-4 timing.
        try
        {
            await _metricsApp.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Best-effort: a Kestrel that's already disposed or stuck past the
            // 5s budget must not prevent IHost shutdown. Genuine bugs in the
            // listener (e.g. port-bind faults) should still surface — those
            // throw other exception types and propagate.
        }
        await _metricsApp.DisposeAsync();

        await _host.StopAsync(TimeSpan.FromSeconds(10));
        _host.Dispose();
    }

    private static async Task<WebApplication> StartMetricsListenerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        // Defang any inherited ASPNETCORE_URLS / DOTNET_URLS — the loopback
        // ephemeral bind below must be the only listener.
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        // Kestrel rejects ListenLocalhost(0); the Listen(IPAddress.Loopback, 0)
        // overload supports dynamic port allocation.
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        // Silence the second host's startup log noise; the IHost above owns
        // the test process's user-facing log surface.
        builder.Logging.ClearProviders();

        var app = builder.Build();
        // MapMetrics with no registry argument binds to Metrics.DefaultRegistry,
        // the same process-global registry PaymentsMeter writes to from the
        // sibling IHost.
        app.MapMetrics("/metrics");

        await app.StartAsync();
        return app;
    }

    private static int ResolveBoundPort(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not surface IServerAddressesFeature.");

        foreach (var address in addresses.Addresses)
        {
            var uri = new Uri(address);
            if (uri.Port > 0)
            {
                return uri.Port;
            }
        }
        throw new InvalidOperationException(
            $"No usable bound address found among [{string.Join(", ", addresses.Addresses)}].");
    }
}
