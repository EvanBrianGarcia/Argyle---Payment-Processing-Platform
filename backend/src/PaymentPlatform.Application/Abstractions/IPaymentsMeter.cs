namespace PaymentPlatform.Application.Abstractions;

/// Application-layer surface for emitting business metrics from handlers.
/// The implementation lives in Infrastructure (wraps prometheus-net); this
/// abstraction keeps the Application project free of any reference to the
/// concrete metrics library, per the Phase 1 layer rule.
public interface IPaymentsMeter
{
    void RecordPaymentCreated(string currency, string merchantId);

    void RecordRefund(string currency);
}
