using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PaymentPlatform.Application.Features.CapturePayment;
using PaymentPlatform.Application.Features.CreatePayment;
using PaymentPlatform.Application.Features.GetPayment;
using PaymentPlatform.Application.Features.ListPayments;
using PaymentPlatform.Application.Features.RefundPayment;
using PaymentPlatform.Contracts.Payments;
using PaymentPlatform.Domain.Payments;
using AppValidationException = PaymentPlatform.Application.Common.ValidationException;
using AppValidationFailure = PaymentPlatform.Application.Common.ValidationFailure;
using NotFoundException = PaymentPlatform.Application.Common.NotFoundException;

namespace PaymentPlatform.Api.Endpoints;

public static class PaymentsEndpoints
{
    public static IEndpointRouteBuilder MapPaymentsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/payments");

        group.MapPost("/", CreatePaymentAsync);
        group.MapGet("/", ListPaymentsAsync);
        group.MapGet("/{id}", GetPaymentAsync);
        group.MapPost("/{id}/capture", CapturePaymentAsync);
        group.MapPost("/{id}/refund", RefundPaymentAsync);

        return routes;
    }

    private const int DefaultListLimit = 20;
    private const int MaxListLimit = 100;

    private static async Task<IResult> ListPaymentsAsync(
        [FromQuery] string? status,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var failures = new List<AppValidationFailure>(2);

        PaymentStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<PaymentStatus>(status, ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                failures.Add(new AppValidationFailure(
                    "status",
                    $"Status '{status}' is not a valid payment status."));
            }
        }

        var effectiveLimit = limit ?? DefaultListLimit;
        if (effectiveLimit < 1 || effectiveLimit > MaxListLimit)
        {
            failures.Add(new AppValidationFailure(
                "limit",
                $"Limit must be between 1 and {MaxListLimit}."));
        }

        if (failures.Count > 0)
        {
            throw new AppValidationException(failures);
        }

        var query = new ListPaymentsQuery(
            Status: statusFilter,
            Cursor: cursor,
            Limit: effectiveLimit);

        var response = await mediator.Send(query, cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> CreatePaymentAsync(
        [FromBody] CreatePaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        IValidator<CreatePaymentCommand> validator,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var command = new CreatePaymentCommand(
            IdempotencyKey: idempotencyKey ?? string.Empty,
            AmountMinor: request.AmountMinor,
            Currency: request.Currency,
            CardToken: request.CardToken,
            CustomerReference: request.CustomerReference,
            Metadata: request.Metadata);

        var result = await validator.ValidateAsync(command, cancellationToken);
        if (!result.IsValid)
        {
            var failures = result.Errors
                .Select(e => new AppValidationFailure(e.PropertyName, e.ErrorMessage))
                .ToList();
            throw new AppValidationException(failures);
        }

        var response = await mediator.Send(command, cancellationToken);
        return Results.Created($"/v1/payments/{response.Id}", response);
    }

    private static async Task<IResult> GetPaymentAsync(
        string id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var response = await mediator.Send(new GetPaymentQuery(id), cancellationToken);
        if (response is null)
        {
            throw new NotFoundException(
                code: "payment_not_found",
                message: $"Payment '{id}' was not found.");
        }
        return Results.Ok(response);
    }

    private static async Task<IResult> CapturePaymentAsync(
        string id,
        [FromBody] CapturePaymentRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        IValidator<CapturePaymentCommand> validator,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var command = new CapturePaymentCommand(
            PaymentId: id,
            IdempotencyKey: idempotencyKey ?? string.Empty,
            AmountMinor: request?.AmountMinor);

        var result = await validator.ValidateAsync(command, cancellationToken);
        if (!result.IsValid)
        {
            var failures = result.Errors
                .Select(e => new AppValidationFailure(e.PropertyName, e.ErrorMessage))
                .ToList();
            throw new AppValidationException(failures);
        }

        var response = await mediator.Send(command, cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> RefundPaymentAsync(
        string id,
        [FromBody] RefundPaymentRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        IValidator<RefundPaymentCommand> validator,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var command = new RefundPaymentCommand(
            PaymentId: id,
            IdempotencyKey: idempotencyKey ?? string.Empty,
            Reason: request?.Reason ?? string.Empty);

        var result = await validator.ValidateAsync(command, cancellationToken);
        if (!result.IsValid)
        {
            var failures = result.Errors
                .Select(e => new AppValidationFailure(e.PropertyName, e.ErrorMessage))
                .ToList();
            throw new AppValidationException(failures);
        }

        var response = await mediator.Send(command, cancellationToken);
        return Results.Ok(response);
    }
}
