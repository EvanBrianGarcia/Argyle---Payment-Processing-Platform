using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Contracts.Payments;

namespace PaymentPlatform.Application.Features.CapturePayment;

public sealed class CapturePaymentCommandHandler : IRequestHandler<CapturePaymentCommand, PaymentResponse>
{
    private const int OkStatusCode = 200;

    private readonly IPaymentsDbContext _db;
    private readonly IdempotencyExecutor _executor;
    private readonly ICurrentMerchant _currentMerchant;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;

    public CapturePaymentCommandHandler(
        IPaymentsDbContext db,
        IdempotencyExecutor executor,
        ICurrentMerchant currentMerchant,
        ICorrelationContext correlation,
        IClock clock)
    {
        _db = db;
        _executor = executor;
        _currentMerchant = currentMerchant;
        _correlation = correlation;
        _clock = clock;
    }

    public Task<PaymentResponse> Handle(
        CapturePaymentCommand command,
        CancellationToken cancellationToken)
    {
        var merchantId = _currentMerchant.MerchantId;

        return _executor.RunAsync(
            merchantId: merchantId,
            operation: IdempotencyOperations.CapturePayment,
            idempotencyKey: command.IdempotencyKey,
            requestHash: ComputeRequestHash(command),
            successStatus: OkStatusCode,
            work: ct => CaptureAsync(merchantId, command, ct),
            cancellationToken: cancellationToken);
    }

    private async Task<PaymentResponse> CaptureAsync(
        string merchantId,
        CapturePaymentCommand command,
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

        // payment.Capture throws InvalidTransitionException on illegal states.
        // The middleware maps it to 409 invalid_state_transition.
        var evt = payment.Capture(_clock.UtcNow);
        _db.PaymentEvents.Add(evt);

        // Settlement is async. Enqueue an outbox row inside the same
        // SaveChangesAsync (driven by IdempotencyExecutor) so the capture and
        // its settlement job commit atomically — ADR-0008.
        var outboxMessage = OutboxMessageFactory.ForSettlement(
            payment: payment,
            correlationId: _correlation.CorrelationId,
            now: _clock.UtcNow);
        _db.PaymentOutbox.Add(outboxMessage);

        var priorEvents = await _db.PaymentEvents
            .AsNoTracking()
            .Where(e => e.PaymentId == payment.Id)
            .OrderBy(e => e.At)
            .ToListAsync(cancellationToken);

        return PaymentResponseSerializer.ToResponse(payment, priorEvents.Append(evt));
    }

    private static string ComputeRequestHash(CapturePaymentCommand command)
    {
        var payload = new
        {
            payment_id = command.PaymentId,
            amount_minor = command.AmountMinor,
        };
        return CanonicalJson.Hash(JsonSerializer.Serialize(payload));
    }
}
