using MediatR;
using PaymentPlatform.Contracts.Payments;

namespace PaymentPlatform.Application.Features.CreatePayment;

public sealed record CreatePaymentCommand(
    string IdempotencyKey,
    long AmountMinor,
    string Currency,
    string CardToken,
    string? CustomerReference,
    IReadOnlyDictionary<string, string>? Metadata) : IRequest<PaymentResponse>;
