using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Infrastructure.Persistence;

namespace PaymentPlatform.IntegrationTests.Fixtures;

/// Seed helpers that drive payments through the state machine via the same
/// API surface a real client uses (POST /v1/payments) plus an in-process
/// aggregate call for Pending → Authorized, mirroring what Phase 3's worker
/// will do.
internal sealed class TestDataBuilder
{
    private readonly PaymentApiFactory _factory;
    private readonly HttpClient _client;

    public TestDataBuilder(PaymentApiFactory factory, HttpClient client)
    {
        _factory = factory;
        _client = client;
    }

    public async Task<string> SeedPendingPaymentAsync(
        string bearer,
        long amountMinor = 1000,
        string currency = "USD",
        string cardToken = "tok_visa_demo")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = TestJson.Content(new
            {
                AmountMinor = amountMinor,
                Currency = currency,
                CardToken = cardToken,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var json = await TestJson.ParseAsync(response);
        return json.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Seed payment response is missing id.");
    }

    public async Task<string> SeedAuthorizedPaymentAsync(
        string bearer,
        long amountMinor = 1000,
        string currency = "USD",
        string cardToken = "tok_visa_demo")
    {
        var id = await SeedPendingPaymentAsync(bearer, amountMinor, currency, cardToken);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var payment = await db.Payments.SingleAsync(p => p.Id == id);
        var evt = payment.Authorize(clock.UtcNow);
        db.PaymentEvents.Add(evt);
        await db.SaveChangesAsync();

        return id;
    }

    public async Task<string> SeedCapturedPaymentAsync(
        string bearer,
        long amountMinor = 1000,
        string currency = "USD",
        string cardToken = "tok_visa_demo")
    {
        var id = await SeedAuthorizedPaymentAsync(bearer, amountMinor, currency, cardToken);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var payment = await db.Payments.SingleAsync(p => p.Id == id);
        var evt = payment.Capture(clock.UtcNow);
        db.PaymentEvents.Add(evt);
        await db.SaveChangesAsync();

        return id;
    }
}
