namespace PaymentPlatform.Messaging.Settlement;

public static class SettlementQueues
{
    public const string Exchange = "payments.settlement";
    public const string Queue = "settlement";
    public const string DeadLetterQueue = "settlement.dlq";
}
