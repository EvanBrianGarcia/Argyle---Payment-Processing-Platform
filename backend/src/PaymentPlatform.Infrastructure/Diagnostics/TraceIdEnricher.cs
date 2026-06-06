using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace PaymentPlatform.Infrastructure.Diagnostics;

/// Phase 4 Task 5 — Serilog enricher that reads the OTel-managed Activity as
/// the source of truth and emits W3C `trace_id` and `span_id` properties
/// onto every log event. Used by both the API and the Worker Serilog
/// pipelines so a single payment's lifecycle correlates across processes.
public sealed class TraceIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("trace_id", activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("span_id", activity.SpanId.ToString()));
    }
}
