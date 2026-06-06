using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PaymentPlatform.Application.Features.CapturePayment;
using PaymentPlatform.Application.Features.CreatePayment;
using PaymentPlatform.Application.Features.GetPayment;
using PaymentPlatform.Application.Features.RefundPayment;
using PaymentPlatform.Contracts.Payments;
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
        group.MapGet("/{id}", GetPaymentAsync);
        group.MapPost("/{id}/capture", CapturePaymentAsync);
        group.MapPost("/{id}/refund", RefundPaymentAsync);

        return routes;
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
