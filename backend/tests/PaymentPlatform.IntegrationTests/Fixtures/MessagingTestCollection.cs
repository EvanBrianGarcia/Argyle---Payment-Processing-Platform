namespace PaymentPlatform.IntegrationTests.Fixtures;

/// Shared Postgres + RabbitMQ container set for the retry / DLQ test pair.
/// One MessagingFixture per test run instead of one per class keeps Docker
/// container churn manageable on slower workstations — back-to-back fresh
/// fixtures had flaked because RabbitMQ struggled to host a fresh consumer
/// connection immediately after the previous broker was torn down.
[CollectionDefinition(Name)]
public sealed class MessagingTestCollection : ICollectionFixture<MessagingFixture>
{
    public const string Name = "Messaging";
}
