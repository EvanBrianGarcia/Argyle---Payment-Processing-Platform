using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace PaymentPlatform.Api.HealthChecks;

/// Short-lived RabbitMQ connectivity probe used by `/health/ready`. Opens a
/// fresh `IConnection`, lets RabbitMQ.Client perform the TCP handshake +
/// authentication round-trip, then closes immediately. Throws on failure so
/// `ProbeAsync` can map the outcome to a healthy/unhealthy result without
/// depending on Microsoft.Extensions.Diagnostics.HealthChecks.
public sealed class RabbitMqHealthProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly string _host;
    private readonly int _port;
    private readonly string _virtualHost;
    private readonly string _username;
    private readonly string _password;

    public RabbitMqHealthProbe(IConfiguration configuration)
    {
        var section = configuration.GetSection("RabbitMq");
        _host = section["Host"] ?? "localhost";
        _port = int.TryParse(section["Port"], out var parsedPort) ? parsedPort : 5672;
        _virtualHost = section["VirtualHost"] ?? "/";
        _username = section["Username"] ?? "guest";
        _password = section["Password"] ?? "guest";
    }

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _host,
            Port = _port,
            VirtualHost = _virtualHost,
            UserName = _username,
            Password = _password,
            RequestedConnectionTimeout = ProbeTimeout,
            ContinuationTimeout = ProbeTimeout,
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            await using var connection = await factory.CreateConnectionAsync(timeoutCts.Token);
            return connection.IsOpen;
        }
        catch
        {
            return false;
        }
    }
}
