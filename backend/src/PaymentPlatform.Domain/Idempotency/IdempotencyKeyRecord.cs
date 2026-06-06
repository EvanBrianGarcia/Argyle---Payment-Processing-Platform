namespace PaymentPlatform.Domain.Idempotency;

public sealed class IdempotencyKeyRecord
{
    public string MerchantId { get; private set; } = default!;
    public string Operation { get; private set; } = default!;
    public string Key { get; private set; } = default!;
    public string RequestHash { get; private set; } = default!;
    public int ResponseStatus { get; private set; }
    public string ResponseBody { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private IdempotencyKeyRecord() { }

    public IdempotencyKeyRecord(
        string merchantId,
        string operation,
        string key,
        string requestHash,
        int responseStatus,
        string responseBody,
        DateTimeOffset createdAt)
    {
        MerchantId = merchantId;
        Operation = operation;
        Key = key;
        RequestHash = requestHash;
        ResponseStatus = responseStatus;
        ResponseBody = responseBody;
        CreatedAt = createdAt;
    }
}
