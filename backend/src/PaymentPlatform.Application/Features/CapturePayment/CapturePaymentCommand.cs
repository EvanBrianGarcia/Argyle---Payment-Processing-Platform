using MediatR;
using PaymentPlatform.Contracts.Payments;

namespace PaymentPlatform.Application.Features.CapturePayment;

public sealed record CapturePaymentCommand(
    string PaymentId,
    string IdempotencyKey,
    long? AmountMinor) : IRequest<PaymentResponse>;
