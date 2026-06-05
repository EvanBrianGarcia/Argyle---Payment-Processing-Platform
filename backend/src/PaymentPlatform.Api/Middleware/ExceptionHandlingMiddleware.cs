using System.Diagnostics;
using System.Text.Json;
using PaymentPlatform.Api.Serialization;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Contracts.Common;
using PaymentPlatform.Domain.Common;

namespace PaymentPlatform.Api.Middleware;

public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationException ex)
        {
            await WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "validation_failed",
                ex.Message,
                details: ex.Failures);
        }
        catch (NotFoundException ex)
        {
            await WriteAsync(
                context,
                StatusCodes.Status404NotFound,
                ex.Code,
                ex.Message,
                details: null);
        }
        catch (IdempotencyConflictException ex)
        {
            await WriteAsync(
                context,
                StatusCodes.Status409Conflict,
                "idempotency_key_conflict",
                ex.Message,
                details: null);
        }
        catch (DomainException ex)
        {
            await WriteAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                ex.Code,
                ex.Message,
                details: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unhandled exception while processing {Method} {Path}",
                context.Request.Method,
                context.Request.Path.Value);

            await WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "internal_error",
                "An unexpected error occurred.",
                details: null);
        }
    }

    private static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        object? details)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var envelope = new ErrorEnvelope(new ErrorBody(
            Code: code,
            Message: message,
            Details: details,
            TraceId: Activity.Current?.TraceId.ToString(),
            RequestId: context.Items[CorrelationIdMiddleware.RequestIdItemKey] as string));

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(envelope, JsonOptions.Default),
            context.RequestAborted);
    }
}
