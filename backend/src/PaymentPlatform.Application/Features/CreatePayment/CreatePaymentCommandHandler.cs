using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Contracts.Payments;
using PaymentPlatform.Domain.Idempotency;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.Application.Features.CreatePayment;

public sealed class CreatePaymentCommandHandler : IRequestHandler<CreatePaymentCommand, PaymentResponse>
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

    public CreatePaymentCommandHandler(
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
        CreatePaymentCommand command,
        CancellationToken cancellationToken)
    {
        var merchantId = _currentMerchant.MerchantId;
        var requestHash = ComputeRequestHash(command);

        var existing = await _idempotency.FindAsync(
            merchantId,
            command.IdempotencyKey,
            cancellationToken);

        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new IdempotencyConflictException();
            }
            return DeserializeResponse(existing.ResponseBody);
        }

        var money = new Money(command.AmountMinor, command.Currency);
        var payment = Payment.Create(
            merchantId: merchantId,
            amount: money,
            cardToken: command.CardToken,
            customerReference: command.CustomerReference,
            metadata: command.Metadata,
            now: _clock.UtcNow);

        _db.Payments.Add(payment);

        var response = ToResponse(payment);
        var responseBody = JsonSerializer.Serialize(response, ResponseJsonOptions);
        var record = new IdempotencyKeyRecord(
            merchantId: merchantId,
            key: command.IdempotencyKey,
            requestHash: requestHash,
            responseStatus: StatusCodes.Created,
            responseBody: responseBody,
            createdAt: _clock.UtcNow);

        try
        {
            await _idempotency.SaveAsync(record, cancellationToken);
        }
        catch (DbUpdateException)
        {
            var winner = await _idempotency.FindAsync(
                merchantId,
                command.IdempotencyKey,
                cancellationToken);

            if (winner is not null)
            {
                return DeserializeResponse(winner.ResponseBody);
            }
            throw;
        }

        return response;
    }

    private static string ComputeRequestHash(CreatePaymentCommand command)
    {
        var payload = new
        {
            amount_minor = command.AmountMinor,
            currency = command.Currency,
            card_token = command.CardToken,
            customer_reference = command.CustomerReference,
            metadata = command.Metadata ?? new Dictionary<string, string>(0),
        };
        return CanonicalJson.Hash(JsonSerializer.Serialize(payload));
    }

    private static PaymentResponse ToResponse(Payment payment) => new(
        Id: payment.Id,
        AmountMinor: payment.Amount.AmountMinor,
        Currency: payment.Amount.Currency,
        Status: payment.Status.ToString(),
        CustomerReference: payment.CustomerReference,
        Metadata: payment.Metadata,
        CreatedAt: payment.CreatedAt);

    private static PaymentResponse DeserializeResponse(string body) =>
        JsonSerializer.Deserialize<PaymentResponse>(body, ResponseJsonOptions)
            ?? throw new InvalidOperationException(
                "Cached idempotency response body could not be deserialized.");

    private static class StatusCodes
    {
        public const int Created = 201;
    }
}
