using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Contracts.Payments;
using PaymentPlatform.Domain.Idempotency;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.Application.Features.CapturePayment;

public sealed class CapturePaymentCommandHandler : IRequestHandler<CapturePaymentCommand, PaymentResponse>
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IPaymentsDbContext _db;
    private readonly IIdempotencyStore _idempotency;
    private readonly ICurrentMerchant _currentMerchant;
    private readonly IClock _clock;

    public CapturePaymentCommandHandler(
        IPaymentsDbContext db,
        IIdempotencyStore idempotency,
        ICurrentMerchant currentMerchant,
        IClock clock)
    {
        _db = db;
        _idempotency = idempotency;
        _currentMerchant = currentMerchant;
        _clock = clock;
    }

    public async Task<PaymentResponse> Handle(
        CapturePaymentCommand command,
        CancellationToken cancellationToken)
    {
        var merchantId = _currentMerchant.MerchantId;
        var requestHash = ComputeRequestHash(command);

        var existing = await _idempotency.FindAsync(
            merchantId,
            IdempotencyOperations.CapturePayment,
            command.IdempotencyKey,
            cancellationToken);

        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new IdempotencyConflictException();
            }
            return PaymentResponseSerializer.Deserialize(existing.ResponseBody);
        }

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

        var priorEvents = await _db.PaymentEvents
            .AsNoTracking()
            .Where(e => e.PaymentId == payment.Id)
            .OrderBy(e => e.At)
            .ToListAsync(cancellationToken);

        var response = PaymentResponseSerializer.ToResponse(payment, priorEvents.Append(evt));
        var responseBody = PaymentResponseSerializer.Serialize(response);

        var record = new IdempotencyKeyRecord(
            merchantId: merchantId,
            operation: IdempotencyOperations.CapturePayment,
            key: command.IdempotencyKey,
            requestHash: requestHash,
            responseStatus: StatusCodes.OK,
            responseBody: responseBody,
            createdAt: _clock.UtcNow);

        try
        {
            // SaveAsync flushes the tracked payment update, the new event row,
            // and the new idempotency row in one transaction.
            await _idempotency.SaveAsync(record, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException();
        }
        catch (DbUpdateException)
        {
            var winner = await _idempotency.FindAsync(
                merchantId,
                IdempotencyOperations.CapturePayment,
                command.IdempotencyKey,
                cancellationToken);

            if (winner is not null)
            {
                return PaymentResponseSerializer.Deserialize(winner.ResponseBody);
            }
            throw;
        }

        return response;
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

    private static class StatusCodes
    {
        public const int OK = 200;
    }
}
