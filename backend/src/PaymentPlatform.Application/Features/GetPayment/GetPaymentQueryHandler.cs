using MediatR;
using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Contracts.Payments;

namespace PaymentPlatform.Application.Features.GetPayment;

public sealed class GetPaymentQueryHandler : IRequestHandler<GetPaymentQuery, PaymentResponse?>
{
    private readonly IPaymentsDbContext _db;
    private readonly ICurrentMerchant _currentMerchant;

    public GetPaymentQueryHandler(IPaymentsDbContext db, ICurrentMerchant currentMerchant)
    {
        _db = db;
        _currentMerchant = currentMerchant;
    }

    public async Task<PaymentResponse?> Handle(
        GetPaymentQuery query,
        CancellationToken cancellationToken)
    {
        var merchantId = _currentMerchant.MerchantId;

        var payment = await _db.Payments
            .AsNoTracking()
            .Where(p => p.MerchantId == merchantId && p.Id == query.PaymentId)
            .FirstOrDefaultAsync(cancellationToken);

        if (payment is null)
        {
            return null;
        }

        var events = await _db.PaymentEvents
            .AsNoTracking()
            .Where(e => e.PaymentId == payment.Id)
            .OrderBy(e => e.At)
            .ToListAsync(cancellationToken);

        return PaymentResponseSerializer.ToResponse(payment, events);
    }
}
