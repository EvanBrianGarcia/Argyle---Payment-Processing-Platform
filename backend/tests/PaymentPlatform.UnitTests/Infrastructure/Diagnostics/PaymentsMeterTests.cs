using System.IO;
using System.Text;
using FluentAssertions;
using PaymentPlatform.Domain.Payments;
using PaymentPlatform.Infrastructure.Diagnostics;
using Prometheus;

namespace PaymentPlatform.UnitTests.Infrastructure.Diagnostics;

/// Phase 4 Task 3 — strongly-typed prometheus-net wrapper.
///
/// The tests construct PaymentsMeter against a private CollectorRegistry so
/// counter/gauge state does not leak between tests (or into the prometheus
/// default registry that the integration suite scrapes). Label cardinality is
/// asserted explicitly — if a future change adds an unbounded label like
/// `payment_id`, these tests fail before the bug reaches the registry.
public sealed class PaymentsMeterTests
{
    private static PaymentsMeter NewMeter() => new(Metrics.NewCustomRegistry());

    [Fact]
    public void RecordPaymentCreated_IncrementsCounterByOne_ForMatchingLabels()
    {
        var registry = Metrics.NewCustomRegistry();
        var meter = new PaymentsMeter(registry);

        meter.RecordPaymentCreated("USD", "mrc_test");

        var body = Scrape(registry);
        body.Should().Contain("payments_created_total{currency=\"USD\",merchant_id=\"mrc_test\"} 1");
    }

    [Fact]
    public void RecordPaymentCreated_TwoMerchantsSameCurrency_KeepsSeparateSeries()
    {
        var registry = Metrics.NewCustomRegistry();
        var meter = new PaymentsMeter(registry);

        meter.RecordPaymentCreated("USD", "mrc_a");
        meter.RecordPaymentCreated("USD", "mrc_a");
        meter.RecordPaymentCreated("USD", "mrc_b");

        var body = Scrape(registry);
        body.Should().Contain("payments_created_total{currency=\"USD\",merchant_id=\"mrc_a\"} 2");
        body.Should().Contain("payments_created_total{currency=\"USD\",merchant_id=\"mrc_b\"} 1");
    }

    [Fact]
    public void RecordRefund_IncrementsRefundsCounter_ByCurrency()
    {
        var registry = Metrics.NewCustomRegistry();
        var meter = new PaymentsMeter(registry);

        meter.RecordRefund("USD");
        meter.RecordRefund("EUR");
        meter.RecordRefund("USD");

        var body = Scrape(registry);
        body.Should().Contain("refunds_total{currency=\"USD\"} 2");
        body.Should().Contain("refunds_total{currency=\"EUR\"} 1");
    }

    [Fact]
    public void SetPaymentStatusCounts_SetsGaugePerStatus_AndIsIdempotent()
    {
        var registry = Metrics.NewCustomRegistry();
        var meter = new PaymentsMeter(registry);

        meter.SetPaymentStatusCounts(new Dictionary<PaymentStatus, long>
        {
            [PaymentStatus.Pending] = 5,
            [PaymentStatus.Authorized] = 3,
            [PaymentStatus.Settled] = 100,
        });

        // Re-setting must replace, not accumulate — gauges are absolute.
        meter.SetPaymentStatusCounts(new Dictionary<PaymentStatus, long>
        {
            [PaymentStatus.Pending] = 7,
            [PaymentStatus.Authorized] = 3,
            [PaymentStatus.Settled] = 100,
        });

        var body = Scrape(registry);
        body.Should().Contain("payments_by_status{status=\"Pending\"} 7");
        body.Should().Contain("payments_by_status{status=\"Authorized\"} 3");
        body.Should().Contain("payments_by_status{status=\"Settled\"} 100");
    }

    [Fact]
    public void RecordSettlementProcessed_AddsHistogramObservation_OnSettlementQueue()
    {
        var registry = Metrics.NewCustomRegistry();
        var meter = new PaymentsMeter(registry);

        meter.RecordSettlementProcessed(TimeSpan.FromMilliseconds(420));

        var body = Scrape(registry);
        body.Should().Contain("mq_processing_duration_seconds_count{queue=\"settlement\"} 1");
        body.Should().Contain("mq_processing_duration_seconds_sum{queue=\"settlement\"}");
    }

    [Fact]
    public void RecordConsumed_IncrementsConsumedCounter_AndAddsDurationObservation()
    {
        var registry = Metrics.NewCustomRegistry();
        var meter = new PaymentsMeter(registry);

        meter.RecordConsumed("settlement", TimeSpan.FromMilliseconds(120));

        var body = Scrape(registry);
        body.Should().Contain("mq_consumed_total{queue=\"settlement\"} 1");
        body.Should().Contain("mq_processing_duration_seconds_count{queue=\"settlement\"} 1");
    }

    [Fact]
    public void RecordRetry_IncrementsRetriesCounter_ByQueue()
    {
        var registry = Metrics.NewCustomRegistry();
        var meter = new PaymentsMeter(registry);

        meter.RecordRetry("settlement");
        meter.RecordRetry("settlement");
        meter.RecordRetry("settlement");

        var body = Scrape(registry);
        body.Should().Contain("mq_retries_total{queue=\"settlement\"} 3");
    }

    [Fact]
    public void RecordDeadLetter_IncrementsDeadletterCounter_ByQueue()
    {
        var registry = Metrics.NewCustomRegistry();
        var meter = new PaymentsMeter(registry);

        meter.RecordDeadLetter("settlement");

        var body = Scrape(registry);
        body.Should().Contain("mq_deadletter_total{queue=\"settlement\"} 1");
    }

    [Fact]
    public void Metrics_DoNotCarryPaymentIdLabel_BoundedCardinalityInvariant()
    {
        var registry = Metrics.NewCustomRegistry();
        var meter = new PaymentsMeter(registry);

        meter.RecordPaymentCreated("USD", "mrc_test");
        meter.RecordRefund("USD");
        meter.RecordConsumed("settlement", TimeSpan.FromMilliseconds(50));
        meter.RecordRetry("settlement");
        meter.RecordDeadLetter("settlement");
        meter.SetPaymentStatusCounts(new Dictionary<PaymentStatus, long>
        {
            [PaymentStatus.Settled] = 1,
        });

        var body = Scrape(registry);
        body.Should().NotContain("payment_id",
            "no metric must carry payment_id as a label — that's the high-cardinality trap from the resume note");
    }

    private static string Scrape(CollectorRegistry registry)
    {
        using var stream = new MemoryStream();
        registry.CollectAndExportAsTextAsync(stream).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
