using MediatR;
using PaymentPlatform.Contracts.Payments;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.Application.Features.ListPayments;

public sealed record ListPaymentsQuery(
    PaymentStatus? Status,
    string? Cursor,
    int Limit) : IRequest<PaymentListResponse>;
