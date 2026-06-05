using MediatR;
using PaymentPlatform.Contracts.Payments;

namespace PaymentPlatform.Application.Features.GetPayment;

public sealed record GetPaymentQuery(string PaymentId) : IRequest<PaymentResponse?>;
