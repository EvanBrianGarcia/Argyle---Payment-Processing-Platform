# ADR-0013: Log redaction is a property-name deny list applied at the Serilog enricher layer

**Status:** Accepted
**Date:** 2026-06-06
**Phase:** 4

## Context

The brief positions this as a payment processor and the README claims a PCI-mindful posture: card data never enters the application beyond a stub `card_token`, the service never sees raw PAN, no CVV handling. The architecture supports that claim — but it relies on engineers not accidentally logging sensitive fields. A future handler that logs `LogInformation("Payment request {@Request}", request)` on a failure path would dump the entire request body, including `card_token`, into stdout.

The primary control is "don't log request bodies" — but the absence-of-logging is fragile defense. A second control is needed: even if a sensitive property name ends up in the property bag of a log event, the serialized output must not contain its value.

The candidates are:

1. **Regex-based redaction over the serialized JSON output.** Runs at the formatter layer.
2. **Property-name deny list at the Serilog enricher layer.** Walks the structured property tree and replaces matching values before the formatter sees them.
3. **Don't log structured objects — only log scalar primitives.** A discipline, not a control.

## Decision

A `RedactingEnricher` (Serilog `ILogEventEnricher`) inspects every `LogEvent`'s property bag, walks structures (`StructureValue`, `DictionaryValue`, `SequenceValue`), and replaces any scalar property whose name (case-insensitive) matches the deny list with the string `"***"`.

The deny list is configured under `Logging:Redaction:DeniedProperties` in `appsettings.json`:

```
[ "card_token", "cvv", "cvc", "pan", "authorization", "api_key", "password", "secret", "token" ]
```

An explicit allow list under `Logging:Redaction:AllowedProperties` overrides deny matches for cases like `trace_token` (matches "token" by substring but is not sensitive):

```
[ "trace_token", "trace_id", "request_id", "correlation_id" ]
```

Matching is exact (case-insensitive) on the property *name*, not substring on the value. The enricher runs before the formatter, so the redaction happens regardless of whether the sink is JSON, plain text, or structured.

The enricher is registered in both `PaymentPlatform.Api` and `PaymentPlatform.Worker` Serilog pipelines after the `TraceIdEnricher`.

## Consequences

- **Defense-in-depth against accidental sensitive-field logging.** A future regression that adds `card_token` to a log event gets stamped `"***"` in the serialized output. The real value is preserved in the request body, the DB, and anywhere else it's intentionally stored — only the *log surface* is redacted.
- **Configuration-driven.** Adding a new sensitive field name is an `appsettings.json` change, not a code change. New environments can extend the list without touching code.
- **Allow-list override prevents false positives.** `trace_token` matches the deny entry `token` exactly, but is allow-listed. Without the override, every trace log line would be redacted.
- **CPU cost is bounded.** The enricher walks every property of every log event. For typical log events (3–10 properties, depth 1–2) the cost is negligible. A separate sanity check: if the enricher encounters a structure deeper than 10 levels, it emits a one-shot warning (likely a bug, but don't crash the log pipeline).
- **Structured value walking covers nested objects and arrays.** A `card_token` inside a `Request.Payment.card_token` object path redacts. A `card_token` inside an array of payments redacts. A `card_token` as a top-level scalar property redacts.
- **The enricher is *not* a substitute for the primary control.** The primary control is "don't log request bodies." The enricher is the safety net.
- **Test coverage asserts the redaction surface.** Unit tests cover deny match, allow-override, case-insensitivity, nested structures, arrays, null values, and the depth limit. Integration tests assert that a `POST /v1/payments` with `card_token` in the body produces a log line where `card_token` is `"***"`.

## Alternatives considered

- **Regex-based redaction at the formatter layer.** Rejected. Operating on the serialized string is fragile: the regex has to know the formatter's escaping rules, key-value delimiter, and structure separator. Worse, a token that matches the regex incidentally (a UUID that looks like a card number) would also get redacted. Structural walking at the property layer is precise.
- **Discipline-only ("don't log request bodies").** Rejected as the *only* control. We keep the discipline (the primary control) and add the enricher as defense-in-depth.
- **Sentry-style scrubber pattern with deep regex against the entire LogEvent.** Rejected. Same fragility as the formatter-layer regex.
- **Per-property `@JsonIgnore`-style attributes on DTOs.** Rejected. Works for objects we own, but the threat is logging an *arbitrary* object that happens to contain a sensitive field — we can't attribute every possible payload.
- **Substring matching on property names.** Rejected. Too greedy. `trace_id` would not be a problem (no deny match), but `authorization_grant_type` matches `authorization`, and an exact case-insensitive match plus an explicit allow list is more predictable.
- **Encrypted logging with field-level keys.** Out of scope for the exercise. Documented as production hardening.
