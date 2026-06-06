using System.Diagnostics;

namespace PaymentPlatform.Api.Middleware;

/// Phase 4 Task 5 — emits a W3C `traceparent` response header derived from
/// the active OTel Activity. Downstream callers (other services, browser
/// clients, CLI tools) can then correlate their next request with the trace
/// id assigned to this one.
///
/// Order matters: this middleware must run AFTER the ASP.NET Core
/// instrumentation has started its server Activity — registering it after
/// `CorrelationIdMiddleware` is enough; the Activity is already current by
/// the time any user middleware runs.
public sealed class TraceparentResponseHeaderMiddleware
{
    public const string HeaderName = "traceparent";

    private readonly RequestDelegate _next;

    public TraceparentResponseHeaderMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task Invoke(HttpContext context)
    {
        // Snapshot the W3C traceparent NOW, while the server Activity is
        // guaranteed to be current. OnStarting fires during response flush,
        // by which time downstream middleware (or the framework's own
        // instrumentation stop) may have already disposed the Activity and
        // cleared Activity.Current.
        var traceparent = Activity.Current?.Id;

        context.Response.OnStarting(static state =>
        {
            var (ctx, value) = ((HttpContext, string?))state;
            if (!string.IsNullOrEmpty(value) && !ctx.Response.Headers.ContainsKey(HeaderName))
            {
                ctx.Response.Headers[HeaderName] = value;
            }
            return Task.CompletedTask;
        }, (context, traceparent));

        return _next(context);
    }
}
