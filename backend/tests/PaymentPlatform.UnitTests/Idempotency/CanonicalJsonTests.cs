using FluentAssertions;
using PaymentPlatform.Application.Common;

namespace PaymentPlatform.UnitTests.Idempotency;

public sealed class CanonicalJsonTests
{
    [Fact]
    public void Canonicalize_ReordersObjectKeysLexicographically()
    {
        var input = """{"b":1,"a":2}""";

        var canonical = CanonicalJson.Canonicalize(input);

        canonical.Should().Be("""{"a":2,"b":1}""");
    }

    [Fact]
    public void Canonicalize_ReordersNestedObjectKeys()
    {
        var input = """{"outer":{"z":1,"y":2,"x":3}}""";

        var canonical = CanonicalJson.Canonicalize(input);

        canonical.Should().Be("""{"outer":{"x":3,"y":2,"z":1}}""");
    }

    [Fact]
    public void Canonicalize_PreservesArrayOrder()
    {
        var input = """{"arr":[3,1,2]}""";

        var canonical = CanonicalJson.Canonicalize(input);

        canonical.Should().Be("""{"arr":[3,1,2]}""");
    }

    [Fact]
    public void Canonicalize_PreservesNull()
    {
        var input = """{"a":null,"b":1}""";

        var canonical = CanonicalJson.Canonicalize(input);

        canonical.Should().Be("""{"a":null,"b":1}""");
    }

    [Fact]
    public void Canonicalize_PreservesBooleans()
    {
        var input = """{"t":true,"f":false}""";

        var canonical = CanonicalJson.Canonicalize(input);

        canonical.Should().Be("""{"f":false,"t":true}""");
    }

    [Fact]
    public void Canonicalize_StripsWhitespace()
    {
        var input = """{ "a" : 1 ,  "b" :  2 }""";

        var canonical = CanonicalJson.Canonicalize(input);

        canonical.Should().Be("""{"a":1,"b":2}""");
    }

    [Fact]
    public void Hash_IdenticalInput_ProducesSameHash()
    {
        var body = """{"amount_minor":1000,"currency":"USD","card_token":"tok_abc"}""";

        var hash1 = CanonicalJson.Hash(body);
        var hash2 = CanonicalJson.Hash(body);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Hash_ReorderedKeys_ProducesSameHash()
    {
        var ordered = """{"amount_minor":1000,"card_token":"tok_abc","currency":"USD"}""";
        var reordered = """{"currency":"USD","card_token":"tok_abc","amount_minor":1000}""";

        var hash1 = CanonicalJson.Hash(ordered);
        var hash2 = CanonicalJson.Hash(reordered);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Hash_ReorderedNestedKeys_ProducesSameHash()
    {
        var first = """{"metadata":{"key2":"v2","key1":"v1"},"amount_minor":100}""";
        var second = """{"amount_minor":100,"metadata":{"key1":"v1","key2":"v2"}}""";

        var hash1 = CanonicalJson.Hash(first);
        var hash2 = CanonicalJson.Hash(second);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Hash_DifferentValues_ProduceDifferentHashes()
    {
        var bodyA = """{"amount_minor":1000,"currency":"USD"}""";
        var bodyB = """{"amount_minor":2000,"currency":"USD"}""";

        var hashA = CanonicalJson.Hash(bodyA);
        var hashB = CanonicalJson.Hash(bodyB);

        hashA.Should().NotBe(hashB);
    }

    [Fact]
    public void Hash_IsLowercaseHex64Chars()
    {
        var body = """{"a":1}""";

        var hash = CanonicalJson.Hash(body);

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Hash_DifferentArrayOrder_ProducesDifferentHash()
    {
        var bodyA = """{"items":[1,2,3]}""";
        var bodyB = """{"items":[3,2,1]}""";

        var hashA = CanonicalJson.Hash(bodyA);
        var hashB = CanonicalJson.Hash(bodyB);

        hashA.Should().NotBe(hashB);
    }
}
