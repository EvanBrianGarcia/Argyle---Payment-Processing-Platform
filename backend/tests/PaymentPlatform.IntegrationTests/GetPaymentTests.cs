using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class GetPaymentTests : IntegrationTestBase
{
    private const string AcmeKey = "dev-key-mrc-acme";
    private const string PiedKey = "dev-key-mrc-pied";
    private const string DefaultRefundReason = "customer_request";

    private readonly TestDataBuilder _seed;

    public GetPaymentTests(PostgresFixture postgres) : base(postgres)
    {
        _seed = new TestDataBuilder(Factory, Client);
    }

    [Fact]
    public async Task Returns200_ForOwningMerchant()
    {
        var paymentId = await CreatePaymentAsync(AcmeKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/payments/{paymentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("id").GetString().Should().Be(paymentId);
        json.RootElement.GetProperty("amount_minor").GetInt64().Should().Be(1000);
        json.RootElement.GetProperty("currency").GetString().Should().Be("USD");
    }

    [Fact]
    public async Task Returns404_ForOtherMerchant_IsolatesCrossTenant()
    {
        var paymentId = await CreatePaymentAsync(AcmeKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/payments/{paymentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", PiedKey);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("payment_not_found");
    }

    [Fact]
    public async Task Returns404_ForUnknownId()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/payments/pay_doesnotexist");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("payment_not_found");
    }

    [Fact]
    public async Task HappyPath_ResponseIncludesEventTimeline()
    {
        var paymentId = await _seed.SeedCapturedPaymentAsync(AcmeKey);

        using var refundRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/payments/{paymentId}/refund")
        {
            Content = TestJson.Content(new { Reason = DefaultRefundReason }),
        };
        refundRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        refundRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var refundResponse = await Client.SendAsync(refundRequest);
        refundResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/payments/{paymentId}");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        var response = await Client.SendAsync(getRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        var events = json.RootElement.GetProperty("events");
        events.GetArrayLength().Should().Be(4, "timeline must include create + authorize + capture + refund");

        var toStatuses = events
            .EnumerateArray()
            .Select(e => e.GetProperty("to_status").GetString())
            .ToArray();
        toStatuses.Should().Equal("Pending", "Authorized", "Captured", "Refunded");

        var timestamps = events
            .EnumerateArray()
            .Select(e => e.GetProperty("at").GetDateTimeOffset())
            .ToArray();
        timestamps.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Returns401_WhenBearerMissing()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/payments/pay_anything");

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("unauthorized");
    }

    private async Task<string> CreatePaymentAsync(string bearer)
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var json = await TestJson.ParseAsync(response);
        return json.RootElement.GetProperty("id").GetString()!;
    }
}
