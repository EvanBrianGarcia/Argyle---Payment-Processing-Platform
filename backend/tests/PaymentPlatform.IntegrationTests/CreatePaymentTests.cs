using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class CreatePaymentTests : IntegrationTestBase
{
    private const string AcmeKey = "dev-key-mrc-acme";

    public CreatePaymentTests(PostgresFixture postgres) : base(postgres)
    {
    }

    [Fact]
    public async Task Returns201_WithPaymentId_OnHappyPath()
    {
        using var request = BuildCreateRequest(
            bearer: AcmeKey,
            idempotencyKey: Guid.NewGuid().ToString(),
            payload: ValidPayload());

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var json = await TestJson.ParseAsync(response);
        var id = json.RootElement.GetProperty("id").GetString();
        id.Should().StartWith("pay_");
        json.RootElement.GetProperty("amount_minor").GetInt64().Should().Be(1000);
        json.RootElement.GetProperty("currency").GetString().Should().Be("USD");

        (await CountPaymentsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task HappyPath_ResponseIncludesInitialPendingEvent()
    {
        using var request = BuildCreateRequest(
            bearer: AcmeKey,
            idempotencyKey: Guid.NewGuid().ToString(),
            payload: ValidPayload());

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var json = await TestJson.ParseAsync(response);
        var paymentId = json.RootElement.GetProperty("id").GetString();
        paymentId.Should().StartWith("pay_");

        var events = json.RootElement.GetProperty("events");
        events.ValueKind.Should().Be(JsonValueKind.Array);
        events.GetArrayLength().Should().Be(1, "create must emit exactly one initial event");

        var initial = events[0];
        // from_status is null on the initial event and the API serializer omits
        // null properties globally, so the property must be absent — not present-as-null.
        initial.TryGetProperty("from_status", out _).Should().BeFalse(
            "the API omits null-valued properties; from_status must not be emitted on the initial event");
        initial.GetProperty("to_status").GetString().Should().Be("Pending");
        initial.GetProperty("reason").GetString().Should().Be("created");
        initial.GetProperty("actor").GetString().Should().Be("api");

        var rowsForPayment = await CountEventsForAsync(paymentId!);
        rowsForPayment.Should().Be(1, "exactly one event row must be persisted on create");
    }

    [Fact]
    public async Task HappyPath_ResponseIncludesUpdatedAt()
    {
        using var request = BuildCreateRequest(
            bearer: AcmeKey,
            idempotencyKey: Guid.NewGuid().ToString(),
            payload: ValidPayload());

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var json = await TestJson.ParseAsync(response);
        var createdAt = json.RootElement.GetProperty("created_at").GetDateTimeOffset();
        var updatedAt = json.RootElement.GetProperty("updated_at").GetDateTimeOffset();
        updatedAt.Should().Be(createdAt, "on initial create updated_at must equal created_at");
    }

    [Fact]
    public async Task Returns201_WithIdenticalBody_OnReplay()
    {
        var idempotencyKey = Guid.NewGuid().ToString();
        var payload = ValidPayload();

        using var first = BuildCreateRequest(AcmeKey, idempotencyKey, payload);
        var firstResponse = await Client.SendAsync(first);
        var firstBody = await firstResponse.Content.ReadAsStringAsync();

        using var second = BuildCreateRequest(AcmeKey, idempotencyKey, payload);
        var secondResponse = await Client.SendAsync(second);
        var secondBody = await secondResponse.Content.ReadAsStringAsync();

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        secondBody.Should().Be(firstBody);

        (await CountPaymentsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Returns400_WhenIdempotencyKeyMissing()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = TestJson.Content(ValidPayload()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var json = await TestJson.ParseAsync(response);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("validation_failed");

        var details = error.GetProperty("details");
        details.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        var fields = details
            .EnumerateArray()
            .Select(d => d.GetProperty("field").GetString())
            .ToArray();
        fields.Should().Contain("IdempotencyKey");
    }

    [Fact]
    public async Task Returns400_WhenCurrencyInvalid()
    {
        using var request = BuildCreateRequest(
            bearer: AcmeKey,
            idempotencyKey: Guid.NewGuid().ToString(),
            payload: new
            {
                AmountMinor = 1000L,
                Currency = "usd",
                CardToken = "tok_visa_demo",
            });

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var json = await TestJson.ParseAsync(response);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("validation_failed");

        var fields = error.GetProperty("details")
            .EnumerateArray()
            .Select(d => d.GetProperty("field").GetString())
            .ToArray();
        fields.Should().Contain("Currency");
    }

    [Fact]
    public async Task Returns401_WhenBearerMissing()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = TestJson.Content(ValidPayload()),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("unauthorized");
    }

    [Fact]
    public async Task Returns401_WhenBearerUnknown()
    {
        using var request = BuildCreateRequest(
            bearer: "junk-token",
            idempotencyKey: Guid.NewGuid().ToString(),
            payload: ValidPayload());

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("unauthorized");
    }

    private static object ValidPayload() => new
    {
        AmountMinor = 1000L,
        Currency = "USD",
        CardToken = "tok_visa_demo",
    };

    private static HttpRequestMessage BuildCreateRequest(
        string bearer,
        string idempotencyKey,
        object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = TestJson.Content(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private async Task<int> CountPaymentsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.Payments.AsNoTracking().CountAsync();
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
