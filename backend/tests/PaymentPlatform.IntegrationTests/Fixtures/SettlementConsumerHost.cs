using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.Worker.Consumers;

namespace PaymentPlatform.IntegrationTests.Fixtures;

/// In-process IHost that wires the real SettlePaymentConsumer against a real
/// PaymentsDbContext (pointed at the shared PostgresFixture) and an in-memory
/// MassTransit test harness. Lets the consumer tests assert state-machine
/// behavior without paying for a RabbitMQ container.
internal sealed class SettlementConsumerHost : IAsyncDisposable
{
    private readonly IHost _host;

    public IServiceProvider Services => _host.Services;
    public ITestHarness Harness { get; }
    public ControllableProcessor Processor { get; }
    public MutableTestClock Clock { get; }

    private SettlementConsumerHost(
        IHost host,
        ITestHarness harness,
        ControllableProcessor processor,
        MutableTestClock clock)
    {
        _host = host;
        Harness = harness;
        Processor = processor;
        Clock = clock;
    }

    public static async Task<SettlementConsumerHost> StartAsync(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder();

        var processor = new ControllableProcessor();
        var clock = new MutableTestClock();

        builder.Services.AddSingleton<IPaymentProcessor>(processor);
        builder.Services.AddSingleton(processor);
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton(clock);

        builder.Services.AddDbContext<PaymentsDbContext>(options =>
            options.UseNpgsql(connectionString));
        builder.Services.AddScoped<IPaymentsDbContext>(sp =>
            sp.GetRequiredService<PaymentsDbContext>());

        builder.Services.AddMassTransitTestHarness(cfg =>
        {
            cfg.AddConsumer<SettlePaymentConsumer>();
        });

        var host = builder.Build();
        await host.StartAsync();
        var harness = host.Services.GetRequiredService<ITestHarness>();
        await harness.Start();

        return new SettlementConsumerHost(host, harness, processor, clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        _host.Dispose();
    }
}
