using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaymentPlatform.Infrastructure.Diagnostics;
using Serilog;

namespace PaymentPlatform.IntegrationTests.Fixtures;

public sealed class PaymentApiFactory : WebApplicationFactory<PaymentPlatform.Api.Program>
{
    private readonly string _connectionString;

    public PaymentApiFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public InMemoryLogSink LogSink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Env vars are reliably read by WebApplication.CreateBuilder's default ConfigurationManager.
        // ConfigureAppConfiguration callbacks from IWebHostBuilder do not consistently override
        // appsettings.json in the WebApplication minimal-hosting model, so we set env vars instead.
        // Override Microsoft.AspNetCore log level to Information so framework "Executing endpoint"
        // logs emit inside CorrelationIdMiddleware's LogContext scope and carry request_id.
        Environment.SetEnvironmentVariable("ConnectionStrings__Payments", _connectionString);
        Environment.SetEnvironmentVariable(
            "Serilog__MinimumLevel__Override__Microsoft.AspNetCore", "Information");

        builder.UseSerilog((ctx, services, cfg) => cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.FromLogContext()
            .Enrich.With<TraceIdEnricher>()
            .Enrich.With(services.GetRequiredService<RedactingEnricher>())
            .WriteTo.Sink(LogSink));

        return base.CreateHost(builder);
    }
}
