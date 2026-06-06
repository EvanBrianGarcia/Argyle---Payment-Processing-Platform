using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

/// Cross-cutting lifecycle test: drives a single payment through every
/// legal state transition (Pending → Authorized → Captured → Refunded)
/// and asserts the resulting event timeline. The per-endpoint tests in
/// Capture/Refund cover the slices in isolation; this test catches
/// event-ordering and actor/reason regressions that only surface when
/// the slices run in sequence.
[Collection(IntegrationTestCollection.Name)]
public sealed class StateMachineEndToEndTests : IntegrationTestBase
{
    private const string AcmeKey = "dev-key-mrc-acme";
    private const string RefundReason = "customer_request";

    public StateMachineEndToEndTests(PostgresFixture postgres) : base(postgres)
    {
    }

    [Fact]
    public async Task FullLifecycle_EmitsFourEventsInOrder()
    {
        // Pending — via the same POST a real client uses.
        var paymentId = await CreatePaymentAsync();

        // Pending → Authorized — driven through the aggregate inside a
        // scope, mirroring what Phase 3's authorization worker will do.
        await AuthorizeInProcessAsync(paymentId);

        // Authorized → Captured — via HTTP.
        await CaptureAsync(paymentId);

        // Captured → Refunded — via HTTP.
        await RefundAsync(paymentId, RefundReason);

        // GET and assert the timeline.
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/payments/{paymentId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        var getResponse = await Client.SendAsync(getRequest);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(getResponse);
        json.RootElement.GetProperty("status").GetString().Should().Be("Refunded");

        var events = json.RootElement.GetProperty("events");
        events.GetArrayLength().Should().Be(4, "lifecycle must emit create + authorize + capture + refund");

        var toStatuses = events
            .EnumerateArray()
            .Select(e => e.GetProperty("to_status").GetString())
            .ToArray();
        toStatuses.Should().Equal("Pending", "Authorized", "Captured", "Refunded");

        var reasons = events
            .EnumerateArray()
            .Select(e => e.GetProperty("reason").GetString())
            .ToArray();
        reasons.Should().Equal("created", "auth_ok", "captured", "refunded");

        var actors = events
            .EnumerateArray()
            .Select(e => e.GetProperty("actor").GetString())
            .ToArray();
        // Create + Capture + Refund are API-triggered; Authorize mirrors a
        // worker-style in-process call which defaults to "system" on the aggregate.
        actors.Should().Equal("api", "system", "api", "api");

        // Initial event omits from_status (null); the three transitions all carry one.
        events[0].TryGetProperty("from_status", out _).Should().BeFalse(
            "initial event has no prior status and the API omits null properties");
        events[1].GetProperty("from_status").GetString().Should().Be("Pending");
        events[2].GetProperty("from_status").GetString().Should().Be("Authorized");
        events[3].GetProperty("from_status").GetString().Should().Be("Captured");

        var timestamps = events
            .EnumerateArray()
            .Select(e => e.GetProperty("at").GetDateTimeOffset())
            .ToArray();
        timestamps.Should().BeInAscendingOrder("the event timeline must be chronological");

        // Refund event must carry the supplied reason on its payload.
        var refundPayload = events[3].GetProperty("payload");
        refundPayload.GetProperty("reason").GetString().Should().Be(RefundReason);

        // Sanity check against the database itself, not just the projection.
        var persistedCount = await CountEventsForAsync(paymentId);
        persistedCount.Should().Be(4, "exactly four event rows must be persisted across the lifecycle");
    }

    private async Task<string> CreatePaymentAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = TestJson.Content(new
            {
                AmountMinor = 1000L,
                Currency = "USD",
                CardToken = "tok_visa_demo",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var json = await TestJson.ParseAsync(response);
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private async Task AuthorizeInProcessAsync(string paymentId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId);
        var evt = payment.Authorize(clock.UtcNow);
        db.PaymentEvents.Add(evt);
        await db.SaveChangesAsync();
    }

    private async Task CaptureAsync(string paymentId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/payments/{paymentId}/capture")
        {
            Content = TestJson.Content(new { }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task RefundAsync(string paymentId, string reason)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/payments/{paymentId}/refund")
        {
            Content = TestJson.Content(new { Reason = reason }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<int> CountEventsForAsync(string paymentId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.PaymentEvents
            .AsNoTracking()
            .CountAsync(e => e.PaymentId == paymentId);
    }
}
