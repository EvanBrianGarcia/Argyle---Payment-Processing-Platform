namespace PaymentPlatform.Infrastructure.Diagnostics;

/// Phase 4 Task 6 — bound from `Logging:Redaction`. Names match ADR-0013
/// case-insensitively. The allow list overrides the deny list so legitimate
/// false positives (e.g. `trace_token`) survive enrichment.
public sealed class RedactionOptions
{
    public const string SectionName = "Logging:Redaction";

    public IReadOnlyList<string> DeniedProperties { get; init; } = new[]
    {
        "card_token",
        "cvv",
        "cvc",
        "pan",
        "authorization",
        "api_key",
        "password",
        "secret",
        "token",
    };

    public IReadOnlyList<string> AllowedProperties { get; init; } = new[]
    {
        "trace_token",
        "trace_id",
        "request_id",
        "correlation_id",
    };
}
