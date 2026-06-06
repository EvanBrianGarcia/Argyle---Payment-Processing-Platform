using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.Messaging.Settlement;
using PaymentPlatform.Worker.Consumers;

namespace PaymentPlatform.IntegrationTests.Fixtures;

/// In-process IHost that mirrors the Worker's production MassTransit wiring
/// against the real RabbitMQ + Postgres fixtures. Substitutes
/// ControllableProcessor + MutableTestClock for the production singletons so
/// retry/DLQ tests can drive deterministic processor outcomes and shortens
/// the retry intervals to milliseconds so the suite finishes in seconds.
internal sealed class SettlementWorkerHost : IAsyncDisposable
{
    private readonly IHost _host;

    public ControllableProcessor Processor { get; }
    public MutableTestClock Clock { get; }
    public SettlementFaultSink Faults { get; }

    private SettlementWorkerHost(
        IHost host,
        ControllableProcessor processor,
        MutableTestClock clock,
        SettlementFaultSink faults)
    {
        _host = host;
        Processor = processor;
        Clock = clock;
        Faults = faults;
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

        builder.Services.AddMassTransit(cfg =>
        {
            cfg.AddConsumer<SettlePaymentConsumer>();

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

        return new SettlementWorkerHost(host, processor, clock, faults);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(10));
        _host.Dispose();
    }
}
