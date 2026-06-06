using MediatR;
using PaymentPlatform.Contracts.Payments;

namespace PaymentPlatform.Application.Features.RefundPayment;

public sealed record RefundPaymentCommand(
    string PaymentId,
    string IdempotencyKey,
    string Reason) : IRequest<PaymentResponse>;
