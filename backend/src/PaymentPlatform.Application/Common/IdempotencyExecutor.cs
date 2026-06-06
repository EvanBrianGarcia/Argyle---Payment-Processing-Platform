using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Contracts.Payments;
using PaymentPlatform.Domain.Idempotency;

namespace PaymentPlatform.Application.Common;

/// Encapsulates the idempotency dance shared by CreatePayment, CapturePayment,
/// and RefundPayment. Three handlers were the signal to extract.
///
/// The work callback is expected to mutate the same scoped IPaymentsDbContext
/// the store uses; SaveChangesAsync at the end of SaveAsync flushes the
/// aggregate update, any new event row, and the idempotency record together
/// in one transaction.
public sealed class IdempotencyExecutor
{
    private readonly IIdempotencyStore _idempotency;
    private readonly IClock _clock;

    public IdempotencyExecutor(IIdempotencyStore idempotency, IClock clock)
    {
        _idempotency = idempotency;
        _clock = clock;
    }

    public async Task<PaymentResponse> RunAsync(
        string merchantId,
        string operation,
        string idempotencyKey,
        string requestHash,
        int successStatus,
        Func<CancellationToken, Task<PaymentResponse>> work,
        CancellationToken cancellationToken)
    {
        var existing = await _idempotency.FindAsync(
            merchantId,
            operation,
            idempotencyKey,
            cancellationToken);

        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new IdempotencyConflictException();
            }
            return PaymentResponseSerializer.Deserialize(existing.ResponseBody);
        }

        var response = await work(cancellationToken);
        var responseBody = PaymentResponseSerializer.Serialize(response);

        var record = new IdempotencyKeyRecord(
            merchantId: merchantId,
            operation: operation,
            key: idempotencyKey,
            requestHash: requestHash,
            responseStatus: successStatus,
            responseBody: responseBody,
            createdAt: _clock.UtcNow);

        try
        {
            await _idempotency.SaveAsync(record, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Optimistic-concurrency loser on the aggregate's version token.
            // Surfaces as 409 concurrent_modification via the middleware.
            throw new ConcurrencyConflictException();
        }
        catch (DbUpdateException)
        {
            // Defensive for same-key races: a parallel writer with the same
            // (merchant, operation, key) committed first. Return their cached
            // response body so the late arrival sees the winner's payload.
            var winner = await _idempotency.FindAsync(
                merchantId,
                operation,
                idempotencyKey,
                cancellationToken);

            if (winner is not null)
            {
                return PaymentResponseSerializer.Deserialize(winner.ResponseBody);
            }
            throw;
        }

        return response;
    }
}
