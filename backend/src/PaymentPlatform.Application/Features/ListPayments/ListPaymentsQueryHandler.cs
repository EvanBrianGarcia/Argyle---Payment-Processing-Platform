using MediatR;
using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Contracts.Payments;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.Application.Features.ListPayments;

public sealed class ListPaymentsQueryHandler : IRequestHandler<ListPaymentsQuery, PaymentListResponse>
{
    private readonly IPaymentsDbContext _db;
    private readonly ICurrentMerchant _currentMerchant;

    public ListPaymentsQueryHandler(IPaymentsDbContext db, ICurrentMerchant currentMerchant)
    {
        _db = db;
        _currentMerchant = currentMerchant;
    }

    public async Task<PaymentListResponse> Handle(
        ListPaymentsQuery query,
        CancellationToken cancellationToken)
    {
        var merchantId = _currentMerchant.MerchantId;

        var baseQuery = _db.Payments
            .AsNoTracking()
            .Where(p => p.MerchantId == merchantId);

        if (query.Status.HasValue)
        {
            var status = query.Status.Value;
            baseQuery = baseQuery.Where(p => p.Status == status);
        }

        if (query.Cursor is not null)
        {
            var decoded = Cursor.Decode(query.Cursor)
                ?? throw new ValidationException(new[]
                {
                    new ValidationFailure("Cursor", "Cursor is malformed."),
                });

            var cursorCreatedAt = decoded.CreatedAt;
            var cursorPaymentId = decoded.PaymentId;
            baseQuery = baseQuery.Where(p =>
                p.CreatedAt < cursorCreatedAt
                || (p.CreatedAt == cursorCreatedAt
                    && string.Compare(p.Id, cursorPaymentId) < 0));
        }

        // +1 row probes for a next page without a second query.
        var rows = await baseQuery
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > query.Limit)
        {
            // Drop the probe row; the last row in `data` becomes the next anchor.
            rows.RemoveAt(rows.Count - 1);
            var anchor = rows[^1];
            nextCursor = Common.Cursor.Encode(anchor.CreatedAt, anchor.Id);
        }

        var data = rows
            .Select(p => PaymentResponseSerializer.ToResponse(p, Array.Empty<PaymentEvent>()))
            .ToList();

        return new PaymentListResponse(data, nextCursor);
    }
}
