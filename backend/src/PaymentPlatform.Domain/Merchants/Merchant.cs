namespace PaymentPlatform.Domain.Merchants;

public sealed class Merchant
{
    public string Id { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string ApiKeyHash { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private Merchant() { }

    public Merchant(string id, string name, string apiKeyHash, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        ApiKeyHash = apiKeyHash;
        CreatedAt = createdAt;
    }
}
