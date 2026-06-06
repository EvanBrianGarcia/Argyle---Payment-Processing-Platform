namespace PaymentPlatform.Infrastructure.Diagnostics;

/// Bind from `Diagnostics:` in configuration. Currently scopes only the
/// PaymentStatusGaugeUpdater cadence — the production default is 30s, but the
/// integration suite overrides to 200ms via the env var so the gauge updater
/// publishes within the test's polling budget.
public sealed class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    public TimeSpan StatusGaugeInterval { get; init; } = TimeSpan.FromSeconds(30);
}
