using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Application.Common;
using PaymentPlatform.Domain.Outbox;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class OutboxWriteOnCaptureTests : IntegrationTestBase
{
    private const string AcmeKey = "dev-key-mrc-acme";

    private readonly TestDataBuilder _seed;

    public OutboxWriteOnCaptureTests(PostgresFixture postgres) : base(postgres)
    {
        _seed = new TestDataBuilder(Factory, Client);
    }

    [Fact]
    public async Task SuccessfulCapture_WritesUndispatchedOutboxRow_WithSettlePaymentPayload()
    {
        var paymentId = await _seed.SeedAuthorizedPaymentAsync(AcmeKey);
        var correlationId = "trace-" + Guid.NewGuid();

        using var request = BuildCaptureRequest(AcmeKey, Guid.NewGuid().ToString(), paymentId, correlationId);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var outboxRows = await GetOutboxRowsForAsync(paymentId);
        outboxRows.Should().HaveCount(1, "exactly one settlement message should land per capture");

        var row = outboxRows[0];
        row.AggregateId.Should().Be(paymentId);
        row.MessageType.Should().Be(OutboxMessageType.Settlement);
        row.CorrelationId.Should().Be(correlationId);
        row.DispatchedAt.Should().BeNull("dispatcher has not run in this test");

        var deserialized = OutboxMessageFactory.DeserializeSettlement(row);
        deserialized.PaymentId.Should().Be(paymentId);
        deserialized.MerchantId.Should().Be("mrc_acme");
        deserialized.CorrelationId.Should().Be(correlationId);
        deserialized.Attempt.Should().Be(1);
    }

    [Fact]
    public async Task Replay_WithSameIdempotencyKey_DoesNotCreateSecondOutboxRow()
    {
        var paymentId = await _seed.SeedAuthorizedPaymentAsync(AcmeKey);
        var idempotencyKey = Guid.NewGuid().ToString();

        using var first = BuildCaptureRequest(AcmeKey, idempotencyKey, paymentId);
        var firstResponse = await Client.SendAsync(first);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var second = BuildCaptureRequest(AcmeKey, idempotencyKey, paymentId);
        var secondResponse = await Client.SendAsync(second);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var outboxRows = await GetOutboxRowsForAsync(paymentId);
        outboxRows.Should().HaveCount(1, "the cached replay must not enqueue a second settlement job");
    }

    [Fact]
    public async Task InvalidStateTransition_CaptureOnPending_WritesNoOutboxRow()
    {
        var paymentId = await _seed.SeedPendingPaymentAsync(AcmeKey);

        using var request = BuildCaptureRequest(AcmeKey, Guid.NewGuid().ToString(), paymentId);
        var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var outboxRows = await GetOutboxRowsForAsync(paymentId);
        outboxRows.Should()
            .BeEmpty("failed capture must roll back the would-be outbox insert atomically with the payment update");
    }

    private HttpRequestMessage BuildCaptureRequest(
        string bearer,
        string idempotencyKey,
        string paymentId,
        string? correlationId = null,
        object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{paymentId}/capture")
        {
            Content = TestJson.Content(body ?? new { }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (correlationId is not null)
        {
            request.Headers.Add("X-Request-Id", correlationId);
        }
        return request;
    }

    private async Task<IReadOnlyList<PaymentOutboxMessage>> GetOutboxRowsForAsync(string paymentId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.PaymentOutbox
            .AsNoTracking()
            .Where(o => o.AggregateId == paymentId)
            .OrderBy(o => o.Id)
            .ToListAsync();
    }
}
