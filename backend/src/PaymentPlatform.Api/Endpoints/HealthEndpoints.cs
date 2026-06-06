using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Api.HealthChecks;
using PaymentPlatform.Infrastructure.Persistence;

namespace PaymentPlatform.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

        routes.MapGet("/health/ready", async (
            PaymentsDbContext db,
            RabbitMqHealthProbe rabbit,
            CancellationToken cancellationToken) =>
        {
            var postgresTask = ProbePostgresAsync(db, cancellationToken);
            var rabbitTask = rabbit.ProbeAsync(cancellationToken);

            var postgresHealthy = await postgresTask;
            var rabbitHealthy = await rabbitTask;

            var checks = new[]
            {
                new { name = "postgres", healthy = postgresHealthy },
                new { name = "rabbitmq", healthy = rabbitHealthy },
            };

            var allHealthy = postgresHealthy && rabbitHealthy;
            var body = new { status = allHealthy ? "healthy" : "unhealthy", checks };
            return allHealthy
                ? Results.Ok(body)
                : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        return routes;
    }

    private static async Task<bool> ProbePostgresAsync(PaymentsDbContext db, CancellationToken ct)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct);
        }
        catch
        {
            return false;
        }
    }
}
