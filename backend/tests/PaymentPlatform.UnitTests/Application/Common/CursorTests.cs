using FluentAssertions;
using PaymentPlatform.Application.Common;

namespace PaymentPlatform.UnitTests.Application.Common;

public sealed class CursorTests
{
    [Fact]
    public void Encode_Decode_RoundTrip_PreservesValues()
    {
        var createdAt = new DateTimeOffset(2026, 6, 1, 12, 34, 56, 789, TimeSpan.Zero);
        var paymentId = "pay_01J9X7R0K2N3P4Q5R6S7T8U9V0";

        var encoded = Cursor.Encode(createdAt, paymentId);
        var decoded = Cursor.Decode(encoded);

        decoded.Should().NotBeNull();
        decoded!.Value.CreatedAt.Should().Be(createdAt);
        decoded.Value.PaymentId.Should().Be(paymentId);
    }

    [Fact]
    public void Encode_Decode_RoundTrip_PreservesSubsecondPrecision()
    {
        // The "O" format preserves ticks; verify a fractional-second timestamp
        // survives the round-trip without truncation.
        var createdAt = new DateTimeOffset(2026, 6, 1, 12, 34, 56, TimeSpan.Zero)
            .AddTicks(1234567);
        var paymentId = "pay_abc";

        var encoded = Cursor.Encode(createdAt, paymentId);
        var decoded = Cursor.Decode(encoded);

        decoded.Should().NotBeNull();
        decoded!.Value.CreatedAt.Should().Be(createdAt);
    }

    [Fact]
    public void Encode_Decode_SurvivesPaymentIdWithSeparatorCharacter()
    {
        // Payment ids are ULIDs and don't contain "|" today, but the cursor
        // must not silently truncate if a future id format includes one.
        var createdAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var paymentId = "pay_with|pipe|inside";

        var encoded = Cursor.Encode(createdAt, paymentId);
        var decoded = Cursor.Decode(encoded);

        decoded.Should().NotBeNull();
        decoded!.Value.PaymentId.Should().Be(paymentId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!!")]
    [InlineData("Zm9v")]                              // base64 of "foo" — no separator
    [InlineData("MjAyNi0wMS0wMQ==")]                  // base64 of a date with no pipe + id
    public void Decode_MalformedInput_ReturnsNull(string cursor)
    {
        var decoded = Cursor.Decode(cursor);
        decoded.Should().BeNull();
    }

    [Fact]
    public void Decode_BadTimestamp_ReturnsNull()
    {
        // base64 of "not-a-date|pay_abc" — separator present, timestamp invalid.
        var bad = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not-a-date|pay_abc"));
        var decoded = Cursor.Decode(bad);
        decoded.Should().BeNull();
    }

    [Fact]
    public void Decode_EmptyPaymentId_ReturnsNull()
    {
        var bad = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("2026-06-01T00:00:00.0000000+00:00|"));
        var decoded = Cursor.Decode(bad);
        decoded.Should().BeNull();
    }
}
