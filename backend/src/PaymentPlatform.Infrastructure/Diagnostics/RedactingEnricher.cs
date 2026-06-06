using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace PaymentPlatform.Infrastructure.Diagnostics;

/// Phase 4 Task 6 — replaces values whose property name matches the
/// `Logging:Redaction` deny list with `"***"`, walking StructureValue,
/// SequenceValue, and DictionaryValue recursively. Allow-listed names
/// (e.g. `trace_token`) survive the deny match. Depth is capped at
/// MaxDepth — beyond that we emit a one-shot SelfLog warning and return
/// the value untouched. We never log from inside Enrich(...) — that
/// would re-enter the enricher chain.
public sealed class RedactingEnricher : ILogEventEnricher
{
    private const string MaskedValue = "***";
    private const int MaxDepth = 10;
    private static int _depthWarningEmitted;

    private readonly HashSet<string> _denied;
    private readonly HashSet<string> _allowed;

    public RedactingEnricher(IOptions<RedactionOptions> options)
    {
        var snapshot = options.Value;
        // Normalize both lists so `card_token` matches `CardToken` and
        // `cardToken` — the `@` destructuring operator emits PascalCase
        // property names from C# DTOs and the redaction list must catch
        // them without enumerating every casing.
        _denied = new HashSet<string>(
            snapshot.DeniedProperties.Select(Normalize),
            StringComparer.Ordinal);
        _allowed = new HashSet<string>(
            snapshot.AllowedProperties.Select(Normalize),
            StringComparer.Ordinal);
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Snapshot the keys because we may rewrite via AddOrUpdateProperty,
        // which mutates the dictionary the LINQ would otherwise enumerate.
        var propertyNames = logEvent.Properties.Keys.ToArray();

        foreach (var name in propertyNames)
        {
            var original = logEvent.Properties[name];

            if (IsDenied(name))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, MaskedValue));
                continue;
            }

            var rewritten = RewriteStructured(original, depth: 1);
            if (!ReferenceEquals(rewritten, original))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, rewritten));
            }
        }
    }

    private bool IsDenied(string name)
    {
        var normalized = Normalize(name);
        return _denied.Contains(normalized) && !_allowed.Contains(normalized);
    }

    private static string Normalize(string name)
    {
        // Strip underscores and lowercase so deny/allow lookups are insensitive
        // to both case AND the snake_case/PascalCase boundary that Serilog's
        // `@` destructuring crosses.
        var buffer = new char[name.Length];
        var length = 0;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '_')
            {
                continue;
            }
            buffer[length++] = char.ToLowerInvariant(c);
        }
        return new string(buffer, 0, length);
    }

    private LogEventPropertyValue RewriteStructured(LogEventPropertyValue value, int depth)
    {
        if (depth > MaxDepth)
        {
            if (Interlocked.CompareExchange(ref _depthWarningEmitted, 1, 0) == 0)
            {
                SelfLog.WriteLine(
                    "RedactingEnricher: structure depth exceeded {0}; leaving subtree unredacted.",
                    MaxDepth);
            }
            return value;
        }

        switch (value)
        {
            case StructureValue structure:
                return RewriteStructure(structure, depth);
            case SequenceValue sequence:
                return RewriteSequence(sequence, depth);
            case DictionaryValue dictionary:
                return RewriteDictionary(dictionary, depth);
            default:
                return value;
        }
    }

    private LogEventPropertyValue RewriteStructure(StructureValue structure, int depth)
    {
        var rewrittenProperties = new List<LogEventProperty>(structure.Properties.Count);
        var changed = false;

        foreach (var property in structure.Properties)
        {
            if (IsDenied(property.Name))
            {
                rewrittenProperties.Add(new LogEventProperty(
                    property.Name,
                    new ScalarValue(MaskedValue)));
                changed = true;
                continue;
            }

            var rewrittenValue = RewriteStructured(property.Value, depth + 1);
            if (!ReferenceEquals(rewrittenValue, property.Value))
            {
                rewrittenProperties.Add(new LogEventProperty(property.Name, rewrittenValue));
                changed = true;
            }
            else
            {
                rewrittenProperties.Add(property);
            }
        }

        return changed
            ? new StructureValue(rewrittenProperties, structure.TypeTag)
            : structure;
    }

    private LogEventPropertyValue RewriteSequence(SequenceValue sequence, int depth)
    {
        var rewrittenElements = new List<LogEventPropertyValue>(sequence.Elements.Count);
        var changed = false;

        foreach (var element in sequence.Elements)
        {
            var rewritten = RewriteStructured(element, depth + 1);
            if (!ReferenceEquals(rewritten, element))
            {
                changed = true;
            }
            rewrittenElements.Add(rewritten);
        }

        return changed ? new SequenceValue(rewrittenElements) : sequence;
    }

    private LogEventPropertyValue RewriteDictionary(DictionaryValue dictionary, int depth)
    {
        var rewrittenPairs = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
        var changed = false;

        foreach (var pair in dictionary.Elements)
        {
            var keyText = pair.Key.Value?.ToString();
            if (keyText is not null && IsDenied(keyText))
            {
                rewrittenPairs.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                    pair.Key,
                    new ScalarValue(MaskedValue)));
                changed = true;
                continue;
            }

            var rewrittenValue = RewriteStructured(pair.Value, depth + 1);
            if (!ReferenceEquals(rewrittenValue, pair.Value))
            {
                changed = true;
            }
            rewrittenPairs.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(pair.Key, rewrittenValue));
        }

        return changed ? new DictionaryValue(rewrittenPairs) : dictionary;
    }
}
