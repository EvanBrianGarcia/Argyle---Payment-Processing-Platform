using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ErrorResponseHeadersTests : IntegrationTestBase
{
    private const string AcmeKey = "dev-key-mrc-acme";

    public ErrorResponseHeadersTests(PostgresFixture postgres) : base(postgres)
    {
    }

    [Fact]
    public async Task NotFoundResponse_CarriesXRequestIdHeader_MatchingBody()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/payments/pay_nonexistent");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);

        var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        response.Headers.TryGetValues("X-Request-Id", out var headerValues)
            .Should().BeTrue("error responses must carry the request id header");
        var headerRequestId = headerValues!.Single();
        headerRequestId.Should().NotBeNullOrWhiteSpace();

        using var json = await TestJson.ParseAsync(response);
        var bodyRequestId = json.RootElement
            .GetProperty("error")
            .GetProperty("request_id")
            .GetString();

        headerRequestId.Should().Be(
            bodyRequestId,
            "header and body must agree on the request id so clients can correlate either way");
    }
}
