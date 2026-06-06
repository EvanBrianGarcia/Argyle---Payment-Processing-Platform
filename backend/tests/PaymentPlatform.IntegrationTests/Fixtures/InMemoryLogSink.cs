using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace PaymentPlatform.IntegrationTests.Fixtures;

public sealed class InMemoryLogSink : ILogEventSink
{
    private readonly ITextFormatter _formatter = new CompactJsonFormatter();
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => _lines.ToArray();

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        _formatter.Format(logEvent, writer);
        var line = writer.ToString().TrimEnd('\r', '\n');
        if (!string.IsNullOrEmpty(line))
        {
            _lines.Enqueue(line);
        }
    }

    public void Clear()
    {
        while (_lines.TryDequeue(out _))
        {
        }
    }
}
