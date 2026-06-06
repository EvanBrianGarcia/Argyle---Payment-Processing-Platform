using System.Diagnostics;

namespace PaymentPlatform.Application.Diagnostics;

/// Single application-level ActivitySource for handler and consumer spans.
/// Auto-instrumentation packages cover HTTP / DB / MQ; this source carries
/// domain context (payment_id, merchant_id) that the framework cannot infer.
/// Subscribed via `AddSource(PaymentsActivitySource.Name)` on the
/// TracerProviderBuilder.
public static class PaymentsActivitySource
{
    public const string Name = "PaymentPlatform.Application";

    public static readonly ActivitySource Source = new(Name);
}
