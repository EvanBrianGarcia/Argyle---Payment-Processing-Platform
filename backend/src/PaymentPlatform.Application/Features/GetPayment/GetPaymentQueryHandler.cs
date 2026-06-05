using MediatR;
using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Application.Abstractions;
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

        return new PaymentResponse(
            Id: payment.Id,
            AmountMinor: payment.Amount.AmountMinor,
            Currency: payment.Amount.Currency,
            Status: payment.Status.ToString(),
            CustomerReference: payment.CustomerReference,
            Metadata: payment.Metadata,
            CreatedAt: payment.CreatedAt);
    }
}
