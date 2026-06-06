using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PaymentPlatform.IntegrationTests.Fixtures;

internal static class TestJson
{
    public static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static StringContent Content(object payload)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(payload, SnakeCase),
            Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    public static async Task<JsonDocument> ParseAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }
}
