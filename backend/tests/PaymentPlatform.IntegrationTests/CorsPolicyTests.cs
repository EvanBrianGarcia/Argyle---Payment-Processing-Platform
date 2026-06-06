using System.Net;
using FluentAssertions;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class CorsPolicyTests : IntegrationTestBase
{
    public CorsPolicyTests(PostgresFixture postgres) : base(postgres)
    {
    }

    [Fact]
    public async Task PreflightFromAllowedOrigin_Returns204_WithExpectedHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/payments");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "Authorization, Content-Type, Idempotency-Key");

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin")
            .Should().ContainSingle().Which.Should().Be("http://localhost:5173");
        response.Headers.GetValues("Access-Control-Allow-Methods")
            .Should().ContainSingle().Which.Should().Contain("POST");
        response.Headers.GetValues("Access-Control-Allow-Headers")
            .Should().ContainSingle().Which.Should().Contain("Idempotency-Key");
    }

    [Fact]
    public async Task PreflightFromDisallowedOrigin_DoesNotEchoOrigin()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/payments");
        request.Headers.Add("Origin", "https://evil.example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await Client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task SimpleRequestFromAllowedOrigin_HasTraceparentExposed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", "http://localhost:5173");

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Expose-Headers")
            .Should().ContainSingle().Which.Should().Contain("traceparent");
    }
}
