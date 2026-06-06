using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Api;
using PaymentPlatform.Api.Configuration;
using PaymentPlatform.Api.Diagnostics;
using PaymentPlatform.Api.Endpoints;
using PaymentPlatform.Api.HealthChecks;
using PaymentPlatform.Api.HostedServices;
using PaymentPlatform.Api.Middleware;
using PaymentPlatform.Infrastructure;
using PaymentPlatform.Infrastructure.Messaging;
using PaymentPlatform.Infrastructure.Persistence;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.With<TraceIdEnricher>()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, _, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .Enrich.With<TraceIdEnricher>()
        .WriteTo.Console(new CompactJsonFormatter()));

    builder.Services.AddApiServices();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddPaymentMessagingPublisher(builder.Configuration);
    builder.Services.AddSingleton<RabbitMqHealthProbe>();

    builder.Services
        .AddOptions<OutboxDispatcherOptions>()
        .Bind(builder.Configuration.GetSection(OutboxDispatcherOptions.SectionName));
    builder.Services.AddHostedService<OutboxDispatcher>();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        db.Database.Migrate();
    }

    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseMiddleware<DevBearerAuthMiddleware>();

    app.MapHealthEndpoints();
    app.MapPaymentsEndpoints();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }
