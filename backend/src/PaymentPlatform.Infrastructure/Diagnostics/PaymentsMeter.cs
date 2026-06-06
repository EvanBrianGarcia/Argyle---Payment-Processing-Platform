using PaymentPlatform.Application.Abstractions;
using PaymentPlatform.Domain.Payments;
using Prometheus;

namespace PaymentPlatform.Infrastructure.Diagnostics;

/// Phase 4 Task 3 — strongly-typed prometheus-net wrapper.
///
/// One field per metric: counters for monotonic counts, gauges for live
/// values, histograms for distribution-shaped observations. Label names are
/// declared once in the constructor so cardinality stays bounded:
///   payments_created_total  → {currency, merchant_id}
///   refunds_total           → {currency}
///   mq_consumed_total       → {queue}
///   mq_retries_total        → {queue}
///   mq_deadletter_total     → {queue}
///   mq_processing_duration  → {queue}
///   payments_by_status      → {status}
///
/// Notably absent: any `payment_id` label. The unit test suite asserts that
/// invariant directly — if a future caller starts pulling payment_id in, the
/// `Metrics_DoNotCarryPaymentIdLabel` regression fires.
///
/// Accepts a CollectorRegistry so unit tests can isolate state in a custom
/// registry. The default (parameterless) constructor binds to the
/// process-global Metrics.DefaultRegistry that the API's `/metrics` endpoint
/// scrapes.
public sealed class PaymentsMeter : IPaymentsMeter
{
    private const string SettlementQueue = "settlement";

    private readonly Counter _paymentsCreated;
    private readonly Counter _refunds;
    private readonly Counter _mqConsumed;
    private readonly Counter _mqRetries;
    private readonly Counter _mqDeadletter;
    private readonly Histogram _mqProcessingDuration;
    private readonly Gauge _paymentsByStatus;

    public PaymentsMeter()
        : this(Metrics.DefaultRegistry)
    {
    }

    public PaymentsMeter(CollectorRegistry registry)
    {
        var factory = Metrics.WithCustomRegistry(registry);

        _paymentsCreated = factory.CreateCounter(
            "payments_created_total",
            "Total payments created, labelled by currency and merchant.",
            new CounterConfiguration { LabelNames = new[] { "currency", "merchant_id" } });

        _refunds = factory.CreateCounter(
            "refunds_total",
            "Total refunds issued, labelled by currency.",
            new CounterConfiguration { LabelNames = new[] { "currency" } });

        _mqConsumed = factory.CreateCounter(
            "mq_consumed_total",
            "Total messages successfully consumed from a queue.",
            new CounterConfiguration { LabelNames = new[] { "queue" } });

        _mqRetries = factory.CreateCounter(
            "mq_retries_total",
            "Total retry attempts triggered by consumer faults.",
            new CounterConfiguration { LabelNames = new[] { "queue" } });

        _mqDeadletter = factory.CreateCounter(
            "mq_deadletter_total",
            "Total messages routed to the dead-letter queue.",
            new CounterConfiguration { LabelNames = new[] { "queue" } });

        _mqProcessingDuration = factory.CreateHistogram(
            "mq_processing_duration_seconds",
            "Time spent processing a queue message (seconds).",
            new HistogramConfiguration
            {
                LabelNames = new[] { "queue" },
                // Bucket boundaries sized for sub-second settlement work; the
                // 5s and 10s upper tail captures DLQ-bound retry tail latency.
                Buckets = new[] { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0 },
            });

        _paymentsByStatus = factory.CreateGauge(
            "payments_by_status",
            "Current count of payments in each lifecycle status.",
            new GaugeConfiguration { LabelNames = new[] { "status" } });
    }

    public void RecordPaymentCreated(string currency, string merchantId) =>
        _paymentsCreated.WithLabels(currency, merchantId).Inc();

    public void RecordRefund(string currency) =>
        _refunds.WithLabels(currency).Inc();

    public void RecordConsumed(string queue, TimeSpan duration)
    {
        _mqConsumed.WithLabels(queue).Inc();
        _mqProcessingDuration.WithLabels(queue).Observe(duration.TotalSeconds);
    }

    public void RecordRetry(string queue) =>
        _mqRetries.WithLabels(queue).Inc();

    public void RecordDeadLetter(string queue) =>
        _mqDeadletter.WithLabels(queue).Inc();

    public void RecordSettlementProcessed(TimeSpan duration) =>
        _mqProcessingDuration.WithLabels(SettlementQueue).Observe(duration.TotalSeconds);

    public void SetPaymentStatusCounts(IReadOnlyDictionary<PaymentStatus, long> counts)
    {
        foreach (var pair in counts)
        {
            _paymentsByStatus.WithLabels(pair.Key.ToString()).Set(pair.Value);
        }
    }
}
