using System.Net;
using FluentAssertions;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class HealthTests : IntegrationTestBase
{
    public HealthTests(PostgresFixture postgres) : base(postgres)
    {
    }

    [Fact]
    public async Task GetLive_Returns200_WithStatusAlive()
    {
        var response = await Client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("status").GetString().Should().Be("alive");
    }

    [Fact]
    public async Task GetLive_DoesNotRequireBearerToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
