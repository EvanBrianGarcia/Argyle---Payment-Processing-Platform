using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Api.Auth;
using PaymentPlatform.Api.Serialization;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Contracts.Common;
using Serilog.Context;

namespace PaymentPlatform.Api.Middleware;

public sealed class DevBearerAuthMiddleware
{
    private const string AuthorizationHeader = "Authorization";
    private const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;

    public DevBearerAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(
        HttpContext context,
        IPaymentsDbContext db,
        CurrentMerchant currentMerchant)
    {
        if (IsHealthPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var token = ExtractBearerToken(context);
        if (token is null)
        {
            await WriteUnauthorizedAsync(context, "unauthorized", "Bearer token is required.");
            return;
        }

        var hash = HashToken(token);
        var merchant = await db.Merchants
            .AsNoTracking()
            .Where(m => m.ApiKeyHash == hash)
            .Select(m => new { m.Id })
            .FirstOrDefaultAsync(context.RequestAborted);

        if (merchant is null)
        {
            await WriteUnauthorizedAsync(context, "unauthorized", "Bearer token is not recognized.");
            return;
        }

        currentMerchant.Set(merchant.Id);

        using (LogContext.PushProperty("merchant_id", merchant.Id))
        {
            await _next(context);
        }
    }

    private static bool IsHealthPath(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractBearerToken(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(AuthorizationHeader, out var raw))
        {
            return null;
        }

        var value = raw.ToString();
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = value[BearerPrefix.Length..].Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task WriteUnauthorizedAsync(
        HttpContext context,
        string code,
        string message)
    {
        var envelope = new ErrorEnvelope(new ErrorBody(
            Code: code,
            Message: message,
            Details: null,
            TraceId: Activity.Current?.TraceId.ToString(),
            RequestId: context.Items[CorrelationIdMiddleware.RequestIdItemKey] as string));

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(envelope, JsonOptions.Default),
            context.RequestAborted);
    }
}
