using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Domain.Idempotency;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class CapturePaymentTests : IntegrationTestBase
{
    private const string AcmeKey = "dev-key-mrc-acme";
    private const string PiedKey = "dev-key-mrc-pied";

    private readonly TestDataBuilder _seed;

    public CapturePaymentTests(PostgresFixture postgres) : base(postgres)
    {
        _seed = new TestDataBuilder(Factory, Client);
    }

    [Fact]
    public async Task Returns200_OnAuthorizedPayment_TransitionsToCapturedAndAppendsEvent()
    {
        var paymentId = await _seed.SeedAuthorizedPaymentAsync(AcmeKey);

        using var request = BuildCaptureRequest(AcmeKey, Guid.NewGuid().ToString(), paymentId);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("id").GetString().Should().Be(paymentId);
        json.RootElement.GetProperty("status").GetString().Should().Be("Captured");
        json.RootElement.TryGetProperty("updated_at", out _).Should().BeTrue(
            "Phase 2 introduces updated_at on the payment response");

        var events = await GetEventsForAsync(paymentId);
        events.Select(e => e.ToStatus).Should().Contain(PaymentStatus.Captured);
        events.Count(e => e.ToStatus == PaymentStatus.Captured).Should().Be(1);
    }

    [Fact]
    public async Task Returns200_WithIdenticalBody_OnReplaySameKeyAndBody_NoNewEventRow()
    {
        var paymentId = await _seed.SeedAuthorizedPaymentAsync(AcmeKey);
        var idempotencyKey = Guid.NewGuid().ToString();

        using var first = BuildCaptureRequest(AcmeKey, idempotencyKey, paymentId);
        var firstResponse = await Client.SendAsync(first);
        var firstBody = await firstResponse.Content.ReadAsStringAsync();

        using var second = BuildCaptureRequest(AcmeKey, idempotencyKey, paymentId);
        var secondResponse = await Client.SendAsync(second);
        var secondBody = await secondResponse.Content.ReadAsStringAsync();

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondBody.Should().Be(firstBody);

        var captureEvents = (await GetEventsForAsync(paymentId))
            .Count(e => e.ToStatus == PaymentStatus.Captured);
        captureEvents.Should().Be(1, "replay must not append a second event row");
    }

    [Fact]
    public async Task Returns409_OnSameKeyDifferentBody_IdempotencyKeyConflict()
    {
        var paymentId = await _seed.SeedAuthorizedPaymentAsync(AcmeKey);
        var idempotencyKey = Guid.NewGuid().ToString();

        using var first = BuildCaptureRequest(AcmeKey, idempotencyKey, paymentId);
        var firstResponse = await Client.SendAsync(first);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Same key, different body.
        using var second = BuildCaptureRequest(
            AcmeKey,
            idempotencyKey,
            paymentId,
            body: new { AmountMinor = 99999L });
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

        using var request = BuildCaptureRequest(AcmeKey, Guid.NewGuid().ToString(), paymentId);
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
    public async Task Returns409_OnAlreadyCapturedPayment_InvalidStateTransition_AppendsNoEvent()
    {
        var paymentId = await _seed.SeedCapturedPaymentAsync(AcmeKey);

        var eventsBefore = await GetEventsForAsync(paymentId);

        using var request = BuildCaptureRequest(AcmeKey, Guid.NewGuid().ToString(), paymentId);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("invalid_state_transition");

        var eventsAfter = await GetEventsForAsync(paymentId);
        eventsAfter.Count.Should().Be(eventsBefore.Count);
    }

    [Fact]
    public async Task Returns404_OnUnknownPaymentId()
    {
        using var request = BuildCaptureRequest(
            AcmeKey,
            Guid.NewGuid().ToString(),
            paymentId: "pay_doesnotexist");

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("payment_not_found");
    }

    [Fact]
    public async Task Returns404_OnCrossMerchant()
    {
        var paymentId = await _seed.SeedAuthorizedPaymentAsync(AcmeKey);

        using var request = BuildCaptureRequest(PiedKey, Guid.NewGuid().ToString(), paymentId);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("payment_not_found");

        // Verify the original merchant's payment is untouched.
        var statusAfter = await GetStatusForAsync(paymentId);
        statusAfter.Should().Be(PaymentStatus.Authorized);
    }

    [Fact]
    public async Task Returns400_WhenIdempotencyKeyMissing()
    {
        var paymentId = await _seed.SeedAuthorizedPaymentAsync(AcmeKey);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/payments/{paymentId}/capture")
        {
            Content = TestJson.Content(new { }),
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
    public async Task ConcurrentCaptures_OneWinsWithCapturedOneLosesWith409_ExactlyOneEventRow()
    {
        var paymentId = await _seed.SeedAuthorizedPaymentAsync(AcmeKey);

        using var requestA = BuildCaptureRequest(AcmeKey, Guid.NewGuid().ToString(), paymentId);
        using var requestB = BuildCaptureRequest(AcmeKey, Guid.NewGuid().ToString(), paymentId);

        // Distinct idempotency keys so the race is on payment state, not on
        // the idempotency-keys PK. The loser may surface as either
        // `concurrent_modification` (both requests loaded version=1 then one
        // SaveChanges raced the other) or `invalid_state_transition` (the
        // first request committed before the second even loaded). Both
        // outcomes are correct safety properties — the contract is exactly
        // one capture succeeds.
        var responses = await Task.WhenAll(
            Client.SendAsync(requestA),
            Client.SendAsync(requestB));

        var statuses = responses.Select(r => (int)r.StatusCode).ToArray();
        statuses.Should().Contain((int)HttpStatusCode.OK);
        statuses.Should().Contain((int)HttpStatusCode.Conflict);

        var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        using var json = JsonDocument.Parse(await loser.Content.ReadAsStringAsync());
        var loserCode = json.RootElement.GetProperty("error").GetProperty("code").GetString();
        loserCode.Should().BeOneOf("concurrent_modification", "invalid_state_transition");

        var captureEvents = (await GetEventsForAsync(paymentId))
            .Count(e => e.ToStatus == PaymentStatus.Captured);
        captureEvents.Should().Be(1, "the loser's transaction must roll back its event row");
    }

    [Fact]
    public async Task ConcurrentCaptures_LoserOnVersionMismatch_LeavesNoOrphanIdempotencyRow()
    {
        // Deterministic version-mismatch test: load two tracked Payment
        // instances in separate scopes (both see version=1), let scope A
        // commit, then scope B attempts SaveChanges and must throw
        // DbUpdateConcurrencyException. The whole transaction rolls back,
        // so neither the event row nor the idempotency row for B survives.
        var paymentId = await _seed.SeedAuthorizedPaymentAsync(AcmeKey);
        const string loserKey = "loser-key-001";

        await using var scopeA = Factory.Services.CreateAsyncScope();
        await using var scopeB = Factory.Services.CreateAsyncScope();

        var dbA = scopeA.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var clock = scopeA.ServiceProvider.GetRequiredService<IClock>();

        var paymentA = await dbA.Payments.SingleAsync(p => p.Id == paymentId);
        var paymentB = await dbB.Payments.SingleAsync(p => p.Id == paymentId);

        dbA.PaymentEvents.Add(paymentA.Capture(clock.UtcNow));
        await dbA.SaveChangesAsync();  // wins → version bumps to 2

        dbB.PaymentEvents.Add(paymentB.Capture(clock.UtcNow));
        dbB.IdempotencyKeys.Add(new IdempotencyKeyRecord(
            merchantId: "mrc_acme",
            operation: "capture_payment",
            key: loserKey,
            requestHash: "hash_loser",
            responseStatus: 200,
            responseBody: "{}",
            createdAt: clock.UtcNow));

        var act = async () => await dbB.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        // Verify rollback: only the winner's event row exists, no loser
        // idempotency row was persisted.
        using var verifyScope = Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var captureEvents = await verifyDb.PaymentEvents.AsNoTracking()
            .CountAsync(e => e.PaymentId == paymentId && e.ToStatus == PaymentStatus.Captured);
        captureEvents.Should().Be(1);

        var orphanRecord = await verifyDb.IdempotencyKeys.AsNoTracking()
            .AnyAsync(r => r.Key == loserKey);
        orphanRecord.Should().BeFalse("the loser's idempotency row must not survive the rollback");
    }

    private static HttpRequestMessage BuildCaptureRequest(
        string bearer,
        string idempotencyKey,
        string paymentId,
        object? body = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/payments/{paymentId}/capture")
        {
            Content = TestJson.Content(body ?? new { }),
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
