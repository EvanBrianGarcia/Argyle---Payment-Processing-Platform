using NUlid;

namespace PaymentPlatform.Domain.Common;

public static class IdGenerator
{
    private const string PaymentPrefix = "pay_";
    private const string MerchantPrefix = "mrc_";
    private const string EventPrefix = "evt_";

    public static string NewPaymentId() => PaymentPrefix + NewUlid();

    public static string NewMerchantId() => MerchantPrefix + NewUlid();

    public static string NewEventId() => EventPrefix + NewUlid();

    private static string NewUlid() => Ulid.NewUlid().ToString();
}
