using System.Net;
using FluentAssertions;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class OpenApiSchemaTests : IntegrationTestBase
{
    public OpenApiSchemaTests(PostgresFixture postgres) : base(postgres)
    {
    }

    [Fact]
    public async Task GetSchema_Returns200_WithExpectedPaths()
    {
        var response = await Client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        var paths = json.RootElement.GetProperty("paths");

        paths.TryGetProperty("/v1/payments", out _).Should().BeTrue();
        paths.TryGetProperty("/v1/payments/{id}", out _).Should().BeTrue();
        paths.TryGetProperty("/v1/payments/{id}/capture", out _).Should().BeTrue();
        paths.TryGetProperty("/v1/payments/{id}/refund", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetSchema_DoesNotRequireBearerToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi/v1.json");

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSchema_ExposesNamedOperationIds()
    {
        var response = await Client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        var paths = json.RootElement.GetProperty("paths");

        paths.GetProperty("/v1/payments")
            .GetProperty("post")
            .GetProperty("operationId")
            .GetString()
            .Should().Be("createPayment");

        paths.GetProperty("/v1/payments")
            .GetProperty("get")
            .GetProperty("operationId")
            .GetString()
            .Should().Be("listPayments");

        paths.GetProperty("/v1/payments/{id}")
            .GetProperty("get")
            .GetProperty("operationId")
            .GetString()
            .Should().Be("getPayment");

        paths.GetProperty("/v1/payments/{id}/capture")
            .GetProperty("post")
            .GetProperty("operationId")
            .GetString()
            .Should().Be("capturePayment");

        paths.GetProperty("/v1/payments/{id}/refund")
            .GetProperty("post")
            .GetProperty("operationId")
            .GetString()
            .Should().Be("refundPayment");
    }
}
