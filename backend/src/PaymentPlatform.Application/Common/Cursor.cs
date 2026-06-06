using System.Globalization;
using System.Text;

namespace PaymentPlatform.Application.Common;

/// Opaque pagination cursor that captures the last row's (CreatedAt, Id) so
/// a follow-up page can select strictly older rows with id as a tiebreaker.
/// Encoded as base64 of "{createdAt:O}|{paymentId}" — clients should treat
/// the value as opaque and round-trip it verbatim.
public static class Cursor
{
    private const char Separator = '|';

    public static string Encode(DateTimeOffset createdAt, string paymentId)
    {
        var timestamp = createdAt.ToString("O", CultureInfo.InvariantCulture);
        var payload = $"{timestamp}{Separator}{paymentId}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    public static (DateTimeOffset CreatedAt, string PaymentId)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(cursor);
        }
        catch (FormatException)
        {
            return null;
        }

        var payload = Encoding.UTF8.GetString(bytes);
        // Split on the FIRST '|' — the round-trip timestamp never contains
        // one, so the rest of the payload is the payment id verbatim, even
        // if a future id format contains the separator character.
        var separatorIndex = payload.IndexOf(Separator);
        if (separatorIndex < 0)
        {
            return null;
        }

        var timestampSpan = payload.AsSpan(0, separatorIndex);
        var paymentId = payload[(separatorIndex + 1)..];

        if (paymentId.Length == 0)
        {
            return null;
        }

        if (!DateTimeOffset.TryParseExact(
                timestampSpan,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var createdAt))
        {
            return null;
        }

        return (createdAt, paymentId);
    }
}
