using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class RefundPaymentTests : IntegrationTestBase
{
    private const string AcmeKey = "dev-key-mrc-acme";
    private const string PiedKey = "dev-key-mrc-pied";
    private const string DefaultReason = "customer_request";

    private readonly TestDataBuilder _seed;

    public RefundPaymentTests(PostgresFixture postgres) : base(postgres)
    {
        _seed = new TestDataBuilder(Factory, Client);
    }

    [Fact]
    public async Task Returns200_OnCapturedPayment_TransitionsToRefundedAndAppendsEvent()
    {
        var paymentId = await _seed.SeedCapturedPaymentAsync(AcmeKey);

        using var request = BuildRefundRequest(AcmeKey, Guid.NewGuid().ToString(), paymentId, DefaultReason);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("id").GetString().Should().Be(paymentId);
        json.RootElement.GetProperty("status").GetString().Should().Be("Refunded");

        var events = await GetEventsForAsync(paymentId);
        var refundEvent = events.SingleOrDefault(e => e.ToStatus == PaymentStatus.Refunded);
        refundEvent.Should().NotBeNull("refund must append exactly one Refunded event");
        refundEvent!.Payload.Should().ContainKey("reason");
        refundEvent.Payload["reason"].Should().Be(DefaultReason);
    }

    [Fact]
    public async Task Returns200_WithIdenticalBody_OnReplaySameKeyAndBody_NoNewEventRow()
    {
        var paymentId = await _seed.SeedCapturedPaymentAsync(AcmeKey);
        var idempotencyKey = Guid.NewGuid().ToString();

        using var first = BuildRefundRequest(AcmeKey, idempotencyKey, paymentId, DefaultReason);
        var firstResponse = await Client.SendAsync(first);
        var firstBody = await firstResponse.Content.ReadAsStringAsync();

        using var second = BuildRefundRequest(AcmeKey, idempotencyKey, paymentId, DefaultReason);
        var secondResponse = await Client.SendAsync(second);
        var secondBody = await secondResponse.Content.ReadAsStringAsync();

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondBody.Should().Be(firstBody);

        var refundEvents = (await GetEventsForAsync(paymentId))
            .Count(e => e.ToStatus == PaymentStatus.Refunded);
        refundEvents.Should().Be(1, "replay must not append a second event row");
    }

    [Fact]
    public async Task Returns409_OnSameKeyDifferentBody_IdempotencyKeyConflict()
    {
        var paymentId = await _seed.SeedCapturedPaymentAsync(AcmeKey);
        var idempotencyKey = Guid.NewGuid().ToString();

        using var first = BuildRefundRequest(AcmeKey, idempotencyKey, paymentId, DefaultReason);
        var firstResponse = await Client.SendAsync(first);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var second = BuildRefundRequest(
            AcmeKey,
            idempotencyKey,
            paymentId,
            reason: "different_reason");
        var secondResponse = await Client.SendAsync(second);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = await TestJson.ParseAsync(secondResponse);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("idempotency_key_conflict");
    }

    [Fact]
    public async Task Returns409_OnPendingPayment_InvalidStateTransition_AppendsNoEvent()
    {
        var paymentId = await _seed.SeedPendingPaymentAsync(AcmeKey);

        var eventsBefore = await GetEventsForAsync(paymentId);

        using var request = BuildRefundRequest(AcmeKey, Guid.NewGuid().ToString(), paymentId, DefaultReason);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("invalid_state_transition");

        var eventsAfter = await GetEventsForAsync(paymentId);
        eventsAfter.Count.Should().Be(eventsBefore.Count, "no event row may be appended on a rejected transition");

        var statusAfter = await GetStatusForAsync(paymentId);
        statusAfter.Should().Be(PaymentStatus.Pending);
    }

    [Fact]
    public async Task Returns409_OnAuthorizedPayment_InvalidStateTransition_AppendsNoEvent()
    {
        var paymentId = await _seed.SeedAuthorizedPaymentAsync(AcmeKey);

        var eventsBefore = await GetEventsForAsync(paymentId);

        using var request = BuildRefundRequest(AcmeKey, Guid.NewGuid().ToString(), paymentId, DefaultReason);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("invalid_state_transition");

        var eventsAfter = await GetEventsForAsync(paymentId);
        eventsAfter.Count.Should().Be(eventsBefore.Count);

        var statusAfter = await GetStatusForAsync(paymentId);
        statusAfter.Should().Be(PaymentStatus.Authorized);
    }

    [Fact]
    public async Task Returns404_OnUnknownPaymentId()
    {
        using var request = BuildRefundRequest(
            AcmeKey,
            Guid.NewGuid().ToString(),
            paymentId: "pay_doesnotexist",
            reason: DefaultReason);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("payment_not_found");
    }

    [Fact]
    public async Task Returns404_OnCrossMerchant()
    {
        var paymentId = await _seed.SeedCapturedPaymentAsync(AcmeKey);

        using var request = BuildRefundRequest(PiedKey, Guid.NewGuid().ToString(), paymentId, DefaultReason);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("payment_not_found");

        var statusAfter = await GetStatusForAsync(paymentId);
        statusAfter.Should().Be(PaymentStatus.Captured);
    }

    [Fact]
    public async Task Returns400_WhenIdempotencyKeyMissing()
    {
        var paymentId = await _seed.SeedCapturedPaymentAsync(AcmeKey);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/payments/{paymentId}/refund")
        {
            Content = TestJson.Content(new { Reason = DefaultReason }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var json = await TestJson.ParseAsync(response);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("validation_failed");

        var fields = error.GetProperty("details")
            .EnumerateArray()
            .Select(d => d.GetProperty("field").GetString())
            .ToArray();
        fields.Should().Contain("IdempotencyKey");
    }

    [Fact]
    public async Task Returns400_WhenReasonMissing()
    {
        var paymentId = await _seed.SeedCapturedPaymentAsync(AcmeKey);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/payments/{paymentId}/refund")
        {
            Content = TestJson.Content(new { }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var json = await TestJson.ParseAsync(response);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("validation_failed");

        var fields = error.GetProperty("details")
            .EnumerateArray()
            .Select(d => d.GetProperty("field").GetString())
            .ToArray();
        fields.Should().Contain("Reason");

        var statusAfter = await GetStatusForAsync(paymentId);
        statusAfter.Should().Be(PaymentStatus.Captured, "validation failure must not transition the payment");
    }

    [Fact]
    public async Task Returns400_WhenReasonIsWhitespaceOnly()
    {
        // The validator uses !string.IsNullOrWhiteSpace, so whitespace-only
        // reasons are a distinct code path from missing/empty.
        var paymentId = await _seed.SeedCapturedPaymentAsync(AcmeKey);

        using var request = BuildRefundRequest(AcmeKey, Guid.NewGuid().ToString(), paymentId, reason: "   ");

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var json = await TestJson.ParseAsync(response);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("validation_failed");

        var fields = error.GetProperty("details")
            .EnumerateArray()
            .Select(d => d.GetProperty("field").GetString())
            .ToArray();
        fields.Should().Contain("Reason");

        var statusAfter = await GetStatusForAsync(paymentId);
        statusAfter.Should().Be(PaymentStatus.Captured);
    }

    private static HttpRequestMessage BuildRefundRequest(
        string bearer,
        string idempotencyKey,
        string paymentId,
        string reason)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/payments/{paymentId}/refund")
        {
            Content = TestJson.Content(new { Reason = reason }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private async Task<IReadOnlyList<PaymentEvent>> GetEventsForAsync(string paymentId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.PaymentEvents
            .AsNoTracking()
            .Where(e => e.PaymentId == paymentId)
            .OrderBy(e => e.At)
            .ToListAsync();
    }

    private async Task<PaymentStatus> GetStatusForAsync(string paymentId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var payment = await db.Payments
            .AsNoTracking()
            .SingleAsync(p => p.Id == paymentId);
        return payment.Status;
    }
}
