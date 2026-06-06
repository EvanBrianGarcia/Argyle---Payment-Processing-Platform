using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using PaymentPlatform.IntegrationTests.Fixtures;

namespace PaymentPlatform.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class LoggingTests : IntegrationTestBase
{
    private const string AcmeKey = "dev-key-mrc-acme";

    public LoggingTests(PostgresFixture postgres) : base(postgres)
    {
    }

    [Fact]
    public async Task LogLines_AreSingleLineJson()
    {
        await PostPaymentAsync("tok_visa_demo");

        var lines = Factory.LogSink.Lines;
        lines.Should().NotBeEmpty();

        foreach (var line in lines)
        {
            line.Should().NotContain("\n", "log lines must be single-line JSON");
            line.Should().NotContain("\r", "log lines must be single-line JSON");

            var parse = () => JsonDocument.Parse(line);
            parse.Should().NotThrow($"every log line must be valid JSON. Got: {line}");
        }
    }

    [Fact]
    public async Task LogLines_IncludeRequestIdForRequestScopedEntries()
    {
        await PostPaymentAsync("tok_visa_demo");

        var lines = Factory.LogSink.Lines;
        lines.Should().NotBeEmpty();

        var entriesWithRequestId = lines
            .Select(line => JsonDocument.Parse(line))
            .Count(doc => doc.RootElement.TryGetProperty("request_id", out _));

        entriesWithRequestId.Should().BeGreaterThan(
            0,
            "request-scoped log entries must carry the correlation request_id");
    }

    [Fact]
    public async Task Response_IncludesXRequestIdHeader()
    {
        var response = await PostPaymentAsync("tok_visa_demo");

        response.Headers.TryGetValues("X-Request-Id", out var values).Should().BeTrue();
        var requestId = values!.Single();
        requestId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task LogLines_RequestCompletionEntry_CarriesRequestId()
    {
        await PostPaymentAsync("tok_visa_demo");

        var lines = Factory.LogSink.Lines;

        var requestCompletionLines = lines
            .Select(line => JsonDocument.Parse(line))
            .Where(doc =>
                doc.RootElement.TryGetProperty("@mt", out var mt) &&
                (mt.GetString() ?? string.Empty).Contains("HTTP "))
            .ToList();

        requestCompletionLines.Should().NotBeEmpty(
            "Serilog.AspNetCore emits an 'HTTP {Method} {Path} responded {Status}' line per request");
        requestCompletionLines.Should().AllSatisfy(doc =>
            doc.RootElement.TryGetProperty("request_id", out _).Should().BeTrue(
                "the request-completed line must inherit the correlation request_id"));
    }

    [Fact]
    public async Task LogLines_IncludeTraceId()
    {
        await PostPaymentAsync("tok_visa_demo");

        var lines = Factory.LogSink.Lines;
        lines.Should().NotBeEmpty();

        var entriesWithTraceId = lines
            .Select(line => JsonDocument.Parse(line))
            .Count(doc => doc.RootElement.TryGetProperty("trace_id", out _));

        entriesWithTraceId.Should().BeGreaterThan(
            0,
            "request-scoped log entries must carry the OpenTelemetry trace_id");
    }

    [Fact]
    public async Task LogLines_NeverContainCardTokenValue()
    {
        const string secret = "tok_secret_xyz_12345";

        var response = await PostPaymentAsync(secret);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var allLogs = string.Join("\n", Factory.LogSink.Lines);
        allLogs.Should().NotContain(
            secret,
            "card token values must never appear in any log line (PII redaction)");
    }

    private async Task<HttpResponseMessage> PostPaymentAsync(string cardToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = TestJson.Content(new
            {
                AmountMinor = 1000L,
                Currency = "USD",
                CardToken = cardToken,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AcmeKey);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await Client.SendAsync(request);
    }
}
