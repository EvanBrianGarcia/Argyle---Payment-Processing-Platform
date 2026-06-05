using NUlid;
using Serilog.Context;

namespace PaymentPlatform.Api.Middleware;

public sealed class CorrelationIdMiddleware
{
    public const string RequestIdHeader = "X-Request-Id";
    public const string RequestIdItemKey = "request_id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context)
    {
        var requestId = ReadOrGenerateRequestId(context);

        context.Items[RequestIdItemKey] = requestId;
        context.Response.Headers[RequestIdHeader] = requestId;

        using (LogContext.PushProperty("request_id", requestId))
        {
            await _next(context);
        }
    }

    private static string ReadOrGenerateRequestId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(RequestIdHeader, out var incoming))
        {
            var value = incoming.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return Ulid.NewUlid().ToString();
    }
}
