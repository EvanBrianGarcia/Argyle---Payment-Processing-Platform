using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace PaymentPlatform.Api.Diagnostics;

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
