using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Options;
using PaymentPlatform.Infrastructure.Diagnostics;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Parsing;

namespace PaymentPlatform.UnitTests.Infrastructure.Diagnostics;

/// Phase 4 Task 6 — proves the redacting enricher honors ADR-0013's deny/allow
/// spec across scalar, structure, sequence, and dictionary log-event values.
/// Tests build LogEvents by hand (no Serilog logger pipeline) so each case
/// isolates a single behavior of `Enrich(...)`.
public sealed class RedactingEnricherTests
{
    [Fact]
    public void Enrich_Replaces_TopLevel_CardToken_WithMaskedValue()
    {
        var enricher = NewEnricher();
        var logEvent = NewEvent(("card_token", new ScalarValue("tok_visa_demo")));

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        ScalarText(logEvent, "card_token").Should().Be("***");
    }

    [Fact]
    public void Enrich_Replaces_Nested_CardToken_InsideStructureValue()
    {
        var enricher = NewEnricher();
        var request = new StructureValue(new[]
        {
            new LogEventProperty("Currency", new ScalarValue("USD")),
            new LogEventProperty("CardToken", new ScalarValue("tok_visa_demo")),
        });
        var logEvent = NewEvent(("Request", request));

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        var rewritten = (StructureValue)logEvent.Properties["Request"];
        ScalarText(rewritten, "CardToken").Should().Be("***");
        ScalarText(rewritten, "Currency").Should().Be("USD");
    }

    [Fact]
    public void Enrich_Replaces_CardToken_InsideArrayOfStructures()
    {
        var enricher = NewEnricher();
        var first = new StructureValue(new[]
        {
            new LogEventProperty("CardToken", new ScalarValue("tok_one")),
        });
        var second = new StructureValue(new[]
        {
            new LogEventProperty("CardToken", new ScalarValue("tok_two")),
        });
        var sequence = new SequenceValue(new LogEventPropertyValue[] { first, second });
        var logEvent = NewEvent(("Payments", sequence));

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        var rewritten = (SequenceValue)logEvent.Properties["Payments"];
        foreach (var element in rewritten.Elements)
        {
            ScalarText((StructureValue)element, "CardToken").Should().Be("***");
        }
    }

    [Fact]
    public void Enrich_PassesThrough_AllowListed_TraceToken()
    {
        // `trace_token` matches the deny entry `token` via the substring intuition
        // operators tend to apply, but the enricher uses EXACT name matching plus
        // the allow list — so an explicit `trace_token` survives.
        var enricher = NewEnricher();
        var logEvent = NewEvent(("trace_token", new ScalarValue("abc-123")));

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        ScalarText(logEvent, "trace_token").Should().Be("abc-123");
    }

    [Fact]
    public void Enrich_PassesThrough_CustomerReference_NotOnDenyList()
    {
        var enricher = NewEnricher();
        var logEvent = NewEvent(("customer_reference", new ScalarValue("cust_42")));

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        ScalarText(logEvent, "customer_reference").Should().Be("cust_42");
    }

    [Theory]
    [InlineData("Card_Token")]
    [InlineData("CARD_TOKEN")]
    [InlineData("cardToken")]
    [InlineData("CardToken")]
    public void Enrich_IsCaseInsensitive(string propertyName)
    {
        // The deny list uses snake_case; PascalCase `@Destructure` produces
        // CardToken; the match must be case-insensitive against both.
        var enricher = NewEnricher();
        var logEvent = NewEvent((propertyName, new ScalarValue("tok_visa_demo")));

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        ScalarText(logEvent, propertyName).Should().Be("***");
    }

    [Fact]
    public void Enrich_LoadsDenyList_FromOptions()
    {
        // Config-driven proof: only `api_secret` is denied, so `card_token`
        // should pass through untouched.
        var options = Options.Create(new RedactionOptions
        {
            DeniedProperties = new[] { "api_secret" },
            AllowedProperties = System.Array.Empty<string>(),
        });
        var enricher = new RedactingEnricher(options);
        var logEvent = NewEvent(
            ("api_secret", new ScalarValue("shhh")),
            ("card_token", new ScalarValue("tok_visa_demo")));

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        ScalarText(logEvent, "api_secret").Should().Be("***");
        ScalarText(logEvent, "card_token").Should().Be("tok_visa_demo");
    }

    [Fact]
    public void Enrich_DoesNotThrow_OnNullPropertyValue()
    {
        var enricher = NewEnricher();
        var logEvent = NewEvent(("card_token", new ScalarValue(null)));

        var act = () => enricher.Enrich(logEvent, new SimplePropertyFactory());

        act.Should().NotThrow();
        ScalarText(logEvent, "card_token").Should().Be("***",
            "even a null-valued scalar with a denied name must be replaced with the mask");
    }

    [Fact]
    public void Enrich_HandlesDictionaryValue()
    {
        var enricher = NewEnricher();
        var dictionary = new DictionaryValue(new[]
        {
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("card_token"),
                new ScalarValue("tok_visa_demo")),
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("customer_reference"),
                new ScalarValue("cust_42")),
        });
        var logEvent = NewEvent(("Metadata", dictionary));

        enricher.Enrich(logEvent, new SimplePropertyFactory());

        var rewritten = (DictionaryValue)logEvent.Properties["Metadata"];
        var asMap = rewritten.Elements.ToDictionary(
            p => (string)p.Key.Value!,
            p => ((ScalarValue)p.Value).Value?.ToString());
        asMap["card_token"].Should().Be("***");
        asMap["customer_reference"].Should().Be("cust_42");
    }

    [Fact]
    public void Enrich_EmitsSelfLog_AndStopsRecursing_AtDepth11()
    {
        var enricher = NewEnricher();
        var selfLogLines = new List<string>();
        SelfLog.Enable(line => selfLogLines.Add(line));
        try
        {
            // Build a 12-deep nested structure: each level has one property
            // named "Inner" pointing at a deeper StructureValue. The deepest
            // level carries a denied `card_token` to confirm the enricher
            // stopped before reaching it.
            var deepest = new StructureValue(new[]
            {
                new LogEventProperty("card_token", new ScalarValue("tok_buried")),
            });
            LogEventPropertyValue current = deepest;
            for (var i = 0; i < 12; i++)
            {
                current = new StructureValue(new[]
                {
                    new LogEventProperty("Inner", current),
                });
            }
            var logEvent = NewEvent(("Root", current));

            enricher.Enrich(logEvent, new SimplePropertyFactory());

            selfLogLines.Should().Contain(
                line => line.Contains("RedactingEnricher: structure depth exceeded"),
                "the depth guard must emit a SelfLog warning rather than crashing the pipeline");

            // The deepest level was not reached, so the buried token must
            // still be present in the rewritten tree.
            var serialized = SerializeStructure(logEvent.Properties["Root"]);
            serialized.Should().Contain("tok_buried",
                "the recursion must stop short of the buried denied property — proving the depth guard cut the walk");
        }
        finally
        {
            SelfLog.Disable();
        }
    }

    private static RedactingEnricher NewEnricher() =>
        new(Options.Create(new RedactionOptions()));

    private static LogEvent NewEvent(params (string Name, LogEventPropertyValue Value)[] properties)
    {
        var template = new MessageTemplateParser().Parse("test");
        var logEvent = new LogEvent(
            timestamp: System.DateTimeOffset.UtcNow,
            level: LogEventLevel.Information,
            exception: null,
            messageTemplate: template,
            properties: System.Array.Empty<LogEventProperty>());

        foreach (var (name, value) in properties)
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(name, value));
        }
        return logEvent;
    }

    private static string? ScalarText(LogEvent logEvent, string name) =>
        ((ScalarValue)logEvent.Properties[name]).Value?.ToString();

    private static string? ScalarText(StructureValue structure, string name)
    {
        var property = structure.Properties.Single(p => p.Name == name);
        return ((ScalarValue)property.Value).Value?.ToString();
    }

    private static string SerializeStructure(LogEventPropertyValue value)
    {
        using var writer = new System.IO.StringWriter();
        value.Render(writer);
        return writer.ToString();
    }

    private sealed class SimplePropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, value is LogEventPropertyValue v ? v : new ScalarValue(value));
    }
}
