using NUlid;
using PaymentPlatform.Application.Abstractions;

namespace PaymentPlatform.Api.Middleware;

/// API-side implementation of <see cref="ICorrelationContext"/>. Reads the
/// request id the CorrelationIdMiddleware pushed into HttpContext.Items.
public sealed class HttpCorrelationContext : ICorrelationContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCorrelationContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string CorrelationId
    {
        get
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx is not null &&
                ctx.Items.TryGetValue(CorrelationIdMiddleware.RequestIdItemKey, out var value) &&
                value is string requestId &&
                !string.IsNullOrWhiteSpace(requestId))
            {
                return requestId;
            }

            // Fallback for code paths outside an HTTP request (background
            // workers without a message envelope). Generating a fresh id is
            // better than empty-string because it still groups any downstream
            // events from the same call.
            return Ulid.NewUlid().ToString();
        }
    }
}
