using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaymentPlatform.Messaging.Settlement;

namespace PaymentPlatform.IntegrationTests.Fixtures;

/// In-process IHost mirroring PaymentPlatform.Worker's MassTransit
/// configuration, but with a RecordingSettlementConsumer so tests can
/// assert delivery. Disposed at the end of each test class.
public sealed class WorkerTestHost : IAsyncDisposable
{
    private readonly IHost _host;
    public SettlementSink Sink { get; }

    private WorkerTestHost(IHost host, SettlementSink sink)
    {
        _host = host;
        Sink = sink;
    }

    public static async Task<WorkerTestHost> StartAsync(MessagingFixture fixture)
    {
        var sink = new SettlementSink();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(sink);

        builder.Services.AddMassTransit(cfg =>
        {
            cfg.AddConsumer<RecordingSettlementConsumer>();

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
                    e.ConfigureConsumer<RecordingSettlementConsumer>(context);
                });
            });
        });

        var host = builder.Build();
        await host.StartAsync();
        return new WorkerTestHost(host, sink);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(10));
        _host.Dispose();
    }
}
