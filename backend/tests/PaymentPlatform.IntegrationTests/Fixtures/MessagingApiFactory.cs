using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using PaymentPlatform.Api.Diagnostics;
using Serilog;

namespace PaymentPlatform.IntegrationTests.Fixtures;

/// WebApplicationFactory for tests that talk to a real RabbitMQ broker.
/// Mirrors PaymentApiFactory's env-var wiring approach and adds the RabbitMq
/// host/port env vars on top of the Postgres connection string.
public sealed class MessagingApiFactory : WebApplicationFactory<PaymentPlatform.Api.Program>
{
    private readonly MessagingFixture _fixture;
    private readonly int? _rabbitMqPortOverride;

    public MessagingApiFactory(MessagingFixture fixture, int? rabbitMqPortOverride = null)
    {
        _fixture = fixture;
        _rabbitMqPortOverride = rabbitMqPortOverride;
    }

    public InMemoryLogSink LogSink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Payments", _fixture.Postgres.ConnectionString);
        Environment.SetEnvironmentVariable("RabbitMq__Host", _fixture.RabbitMq.Host);
        Environment.SetEnvironmentVariable(
            "RabbitMq__Port",
            (_rabbitMqPortOverride ?? _fixture.RabbitMq.Port).ToString());
        Environment.SetEnvironmentVariable("RabbitMq__Username", _fixture.RabbitMq.Username);
        Environment.SetEnvironmentVariable("RabbitMq__Password", _fixture.RabbitMq.Password);
        Environment.SetEnvironmentVariable(
            "Serilog__MinimumLevel__Override__Microsoft.AspNetCore", "Information");

        builder.UseSerilog((ctx, _, cfg) => cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.FromLogContext()
            .Enrich.With<TraceIdEnricher>()
            .WriteTo.Sink(LogSink));

        return base.CreateHost(builder);
    }
}
