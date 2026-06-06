using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Persistence;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ListPaymentsTests : IntegrationTestBase
{
    private const string AcmeKey = "dev-key-mrc-acme";
    private const string PiedKey = "dev-key-mrc-pied";
    private const string AcmeMerchantId = "mrc_acme";

    private readonly TestDataBuilder _seed;

    public ListPaymentsTests(PostgresFixture postgres) : base(postgres)
    {
        _seed = new TestDataBuilder(Factory, Client);
    }

    [Fact]
    public async Task EmptyList_ReturnsEmptyDataAndNullCursor()
    {
        using var request = BuildListRequest(AcmeKey);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
        HasNextCursor(json.RootElement).Should().BeFalse();
    }

    [Fact]
    public async Task SinglePage_AllReturnedInCreatedAtDescOrder_NextCursorIsNull()
    {
        var first = await _seed.SeedPendingPaymentAsync(AcmeKey);
        var second = await _seed.SeedPendingPaymentAsync(AcmeKey);
        var third = await _seed.SeedPendingPaymentAsync(AcmeKey);

        using var request = BuildListRequest(AcmeKey, limit: 10);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        var data = json.RootElement.GetProperty("data");
        data.GetArrayLength().Should().Be(3);

        // Most-recent first: third, second, first.
        var ids = data.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
        ids.Should().Equal(third, second, first);

        HasNextCursor(json.RootElement).Should().BeFalse();
    }

    [Fact]
    public async Task MultiPage_LimitTwo_ReturnsAllRowsExactlyOnceAcrossPages()
    {
        var ids = new List<string>(5);
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await _seed.SeedPendingPaymentAsync(AcmeKey));
        }

        var collected = new List<string>();
        string? cursor = null;
        var pageCount = 0;

        do
        {
            using var request = BuildListRequest(AcmeKey, limit: 2, cursor: cursor);
            var response = await Client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var json = await TestJson.ParseAsync(response);
            var pageIds = json.RootElement.GetProperty("data")
                .EnumerateArray()
                .Select(e => e.GetProperty("id").GetString()!)
                .ToArray();

            collected.AddRange(pageIds);
            cursor = ReadNextCursor(json.RootElement);
            pageCount++;
        } while (cursor is not null && pageCount < 10);

        pageCount.Should().Be(3, "5 rows at limit=2 → pages of 2, 2, 1");
        collected.Should().HaveCount(5);
        collected.Should().OnlyHaveUniqueItems();
        collected.Should().BeEquivalentTo(ids);

        // Newest first: ids[4], ids[3], ids[2], ids[1], ids[0].
        collected.Should().ContainInOrder(ids[4], ids[3], ids[2], ids[1], ids[0]);
    }

    [Fact]
    public async Task StatusFilter_ReturnsOnlyMatchingPayments()
    {
        // Two pending, one authorized via the test-data builder.
        var pendingA = await _seed.SeedPendingPaymentAsync(AcmeKey);
        var authorized = await _seed.SeedAuthorizedPaymentAsync(AcmeKey);
        var pendingB = await _seed.SeedPendingPaymentAsync(AcmeKey);

        using var request = BuildListRequest(AcmeKey, status: "Pending");
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        var ids = json.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToArray();

        ids.Should().BeEquivalentTo(new[] { pendingA, pendingB });
        ids.Should().NotContain(authorized);
    }

    [Fact]
    public async Task CrossMerchant_DoesNotLeakOtherMerchantsPayments()
    {
        var acme = await _seed.SeedPendingPaymentAsync(AcmeKey);
        var pied = await _seed.SeedPendingPaymentAsync(PiedKey);

        using var request = BuildListRequest(PiedKey);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = await TestJson.ParseAsync(response);
        var ids = json.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToArray();

        ids.Should().Equal(pied);
        ids.Should().NotContain(acme);
    }

    [Fact]
    public async Task BadCursor_Returns400ValidationFailed()
    {
        using var request = BuildListRequest(AcmeKey, cursor: "not-a-valid-cursor!!");
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("validation_failed");
    }

    [Fact]
    public async Task BadStatusFilter_Returns400ValidationFailed()
    {
        using var request = BuildListRequest(AcmeKey, status: "Wishlist");
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("validation_failed");
    }

    [Fact]
    public async Task TimestampTie_AllRowsAppearExactlyOnceAcrossPages()
    {
        // Seed three payments with the SAME created_at via direct DbContext
        // insertion. The cursor must use id as a tiebreaker so pagination
        // stays stable and exhaustive.
        var fixedTime = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var ids = await SeedAcmePaymentsWithFixedTimestampAsync(fixedTime, count: 3);

        var collected = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            using var request = BuildListRequest(AcmeKey, limit: 1, cursor: cursor);
            var response = await Client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var json = await TestJson.ParseAsync(response);
            var pageIds = json.RootElement.GetProperty("data")
                .EnumerateArray()
                .Select(e => e.GetProperty("id").GetString()!)
                .ToArray();

            collected.AddRange(pageIds);
            cursor = ReadNextCursor(json.RootElement);
            pages++;
        } while (cursor is not null && pages < 10);

        collected.Should().HaveCount(3);
        collected.Should().OnlyHaveUniqueItems();
        collected.Should().BeEquivalentTo(ids);

        // ULIDs sort lexicographically; with identical timestamps the cursor
        // tiebreaker orders ids DESC.
        var expectedOrder = ids.OrderByDescending(id => id, StringComparer.Ordinal).ToArray();
        collected.Should().ContainInOrder(expectedOrder);
    }

    [Fact]
    public async Task LimitAbove100_Returns400ValidationFailed()
    {
        using var request = BuildListRequest(AcmeKey, limit: 101);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var json = await TestJson.ParseAsync(response);
        json.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("validation_failed");
    }

    [Fact]
    public async Task ListResponse_DoesNotIncludeEventsArray()
    {
        // List endpoint omits the events timeline to avoid N+1; clients fetch
        // GET /v1/payments/{id} for the full history.
        await _seed.SeedPendingPaymentAsync(AcmeKey);

        using var request = BuildListRequest(AcmeKey);
        var response = await Client.SendAsync(request);

        using var json = await TestJson.ParseAsync(response);
        var row = json.RootElement.GetProperty("data").EnumerateArray().First();

        row.TryGetProperty("events", out var eventsProp).Should().BeTrue();
        eventsProp.GetArrayLength().Should().Be(0);
    }

    private async Task<IReadOnlyList<string>> SeedAcmePaymentsWithFixedTimestampAsync(
        DateTimeOffset at,
        int count)
    {
        var ids = new List<string>(count);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        for (var i = 0; i < count; i++)
        {
            var payment = Payment.Create(
                merchantId: AcmeMerchantId,
                amount: new Money(1000, "USD"),
                cardToken: "tok_visa_demo",
                customerReference: null,
                metadata: null,
                now: at);

            db.Payments.Add(payment);
            ids.Add(payment.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private static bool HasNextCursor(JsonElement root) =>
        ReadNextCursor(root) is not null;

    private static string? ReadNextCursor(JsonElement root)
    {
        if (!root.TryGetProperty("next_cursor", out var prop))
        {
            return null;
        }
        return prop.ValueKind == JsonValueKind.Null ? null : prop.GetString();
    }

    private static HttpRequestMessage BuildListRequest(
        string bearer,
        string? status = null,
        string? cursor = null,
        int? limit = null)
    {
        var query = new List<string>(3);
        if (status is not null) query.Add($"status={Uri.EscapeDataString(status)}");
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        if (limit is not null) query.Add($"limit={limit.Value}");

        var url = "/v1/payments";
        if (query.Count > 0) url += "?" + string.Join("&", query);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }
}
