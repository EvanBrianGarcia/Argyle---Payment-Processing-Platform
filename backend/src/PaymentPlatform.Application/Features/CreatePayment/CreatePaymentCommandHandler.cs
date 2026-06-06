using System.Text.Json;
using MediatR;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Application.Diagnostics;
using PaymentPlatform.Contracts.Payments;
using PaymentPlatform.Domain.Payments;

namespace PaymentPlatform.Application.Features.CreatePayment;

public sealed class CreatePaymentCommandHandler : IRequestHandler<CreatePaymentCommand, PaymentResponse>
{
    private const int CreatedStatusCode = 201;

    private readonly IPaymentsDbContext _db;
    private readonly IdempotencyExecutor _executor;
    private readonly ICurrentMerchant _currentMerchant;
    private readonly IClock _clock;

    public CreatePaymentCommandHandler(
        IPaymentsDbContext db,
        IdempotencyExecutor executor,
        ICurrentMerchant currentMerchant,
        IClock clock)
    {
        _db = db;
        _executor = executor;
        _currentMerchant = currentMerchant;
        _clock = clock;
    }

    public Task<PaymentResponse> Handle(
        CreatePaymentCommand command,
        CancellationToken cancellationToken)
    {
        var merchantId = _currentMerchant.MerchantId;

        return _executor.RunAsync(
            merchantId: merchantId,
            operation: IdempotencyOperations.CreatePayment,
            idempotencyKey: command.IdempotencyKey,
            requestHash: ComputeRequestHash(command),
            successStatus: CreatedStatusCode,
            work: ct => CreateAsync(merchantId, command, ct),
            cancellationToken: cancellationToken);
    }

    private Task<PaymentResponse> CreateAsync(
        string merchantId,
        CreatePaymentCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = PaymentsActivitySource.Source.StartActivity("CreatePayment.Handle");
        activity?.SetTag("merchant_id", merchantId);
        activity?.SetTag("currency", command.Currency);

        var money = new Money(command.AmountMinor, command.Currency);
        var payment = Payment.Create(
            merchantId: merchantId,
            amount: money,
            cardToken: command.CardToken,
            customerReference: command.CustomerReference,
            metadata: command.Metadata,
            now: _clock.UtcNow);

        _db.Payments.Add(payment);
        activity?.SetTag("payment_id", payment.Id);

        var initialEvent = payment.CreateInitialEvent(_clock.UtcNow);
        _db.PaymentEvents.Add(initialEvent);

        var response = PaymentResponseSerializer.ToResponse(payment, new[] { initialEvent });
        return Task.FromResult(response);
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
}
