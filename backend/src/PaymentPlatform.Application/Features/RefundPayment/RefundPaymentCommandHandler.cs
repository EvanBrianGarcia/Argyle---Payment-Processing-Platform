using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Contracts.Payments;

namespace PaymentPlatform.Application.Features.RefundPayment;

public sealed class RefundPaymentCommandHandler : IRequestHandler<RefundPaymentCommand, PaymentResponse>
{
    private const int OkStatusCode = 200;

    private readonly IPaymentsDbContext _db;
    private readonly IdempotencyExecutor _executor;
    private readonly ICurrentMerchant _currentMerchant;
    private readonly IClock _clock;
    private readonly IPaymentsMeter _meter;

    public RefundPaymentCommandHandler(
        IPaymentsDbContext db,
        IdempotencyExecutor executor,
        ICurrentMerchant currentMerchant,
        IClock clock,
        IPaymentsMeter meter)
    {
        _db = db;
        _executor = executor;
        _currentMerchant = currentMerchant;
        _clock = clock;
        _meter = meter;
    }

    public Task<PaymentResponse> Handle(
        RefundPaymentCommand command,
        CancellationToken cancellationToken)
    {
        var merchantId = _currentMerchant.MerchantId;

        return _executor.RunAsync(
            merchantId: merchantId,
            operation: IdempotencyOperations.RefundPayment,
            idempotencyKey: command.IdempotencyKey,
            requestHash: ComputeRequestHash(command),
            successStatus: OkStatusCode,
            work: ct => RefundAsync(merchantId, command, ct),
            cancellationToken: cancellationToken);
    }

    private async Task<PaymentResponse> RefundAsync(
        string merchantId,
        RefundPaymentCommand command,
        CancellationToken cancellationToken)
    {
        // Tracking load — optimistic concurrency on payments.version requires
        // EF to retain the original row version.
        var payment = await _db.Payments
            .Where(p => p.MerchantId == merchantId && p.Id == command.PaymentId)
            .FirstOrDefaultAsync(cancellationToken);

        if (payment is null)
        {
            throw new NotFoundException(
                code: "payment_not_found",
                message: $"Payment '{command.PaymentId}' was not found.");
        }

        // payment.Refund throws InvalidTransitionException on illegal states.
        // The middleware maps it to 409 invalid_state_transition.
        var evt = payment.Refund(_clock.UtcNow, command.Reason);
        _db.PaymentEvents.Add(evt);

        var priorEvents = await _db.PaymentEvents
            .AsNoTracking()
            .Where(e => e.PaymentId == payment.Id)
            .OrderBy(e => e.At)
            .ToListAsync(cancellationToken);

        _meter.RecordRefund(payment.Amount.Currency);
        return PaymentResponseSerializer.ToResponse(payment, priorEvents.Append(evt));
    }

    private static string ComputeRequestHash(RefundPaymentCommand command)
    {
        var payload = new
        {
            payment_id = command.PaymentId,
            reason = command.Reason,
        };
        return CanonicalJson.Hash(JsonSerializer.Serialize(payload));
    }
}
