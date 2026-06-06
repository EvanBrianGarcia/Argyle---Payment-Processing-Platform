# ADR-0012: prometheus-net for /metrics, not the OpenTelemetry Prometheus exporter

**Status:** Accepted
**Date:** 2026-06-06
**Phase:** 4

## Context

The brief asks for a Prometheus-format `/metrics` endpoint exposing three families:

- **RED** — request rate, error rate, duration histograms for the HTTP surface.
- **Business** — `payments_created_total`, `payments_by_status`, `payments_failed_total`, `refunds_total`.
- **Queue** — `mq_queue_depth`, `mq_processing_lag_seconds`, `mq_retries_total`, `mq_deadletter_total`.

We already register the OpenTelemetry SDK for traces (ADR-0011). OTel has a Prometheus exporter (`OpenTelemetry.Exporter.Prometheus.AspNetCore`) — using it would keep the telemetry surface unified under one SDK. The alternative is `prometheus-net.AspNetCore`, a long-standing .NET library that owns the `/metrics` story for the majority of production .NET services.

## Decision

Use `prometheus-net.AspNetCore` to expose `/metrics`. Custom counters, gauges, and histograms are defined on `Metrics.DefaultRegistry`. The OpenTelemetry SDK handles **traces** only; metrics ride a separate prometheus-net pipeline.

Both pipelines coexist:

- OTel SDK → `Activity.Current` → console / OTLP exporter (traces).
- prometheus-net → `Metrics.DefaultRegistry` → `app.MapMetrics("/metrics")` (text/plain exposition).

Both read `Activity.Current.TraceId` when they need to correlate, so there's no correlation lossage between them.

Metric registration lives in `PaymentPlatform.Infrastructure/Diagnostics/PaymentsMeter.cs` — strongly-typed wrappers around `Metrics.CreateCounter<...>(...)` with labels declared explicitly (e.g. `new[] { "currency", "merchant_id" }`). High-cardinality labels (payment_id, request_id, trace_id) are not used as metric labels.

Worker hosts its own `/metrics` on a separate port (default 9090) — see ADR-0012's consequences and the Phase 4 plan §6 / Task 4.

## Consequences

- **Two telemetry pipelines, one trace id.** OTel for traces, prometheus-net for metrics. Both pull trace id from `Activity.Current` when correlating; neither is the source of truth for the other.
- **prometheus-net is the mature .NET workhorse.** API has been stable across major versions, the registry is dead-simple to inspect from tests, and the AspNetCore integration is one extension method (`MapMetrics`).
- **Strongly-typed metric definitions in one place.** `PaymentsMeter.cs` owns every counter/gauge/histogram. Adding a metric is one entry there, not scattered across handlers.
- **Label cardinality is bounded by design.** Labels are declared explicitly at metric construction; we can't accidentally introduce a payment_id label by passing the wrong dictionary. Bounded labels: `currency` (~5), `status` (~6), `merchant_id` (~500). No unbounded labels.
- **Worker's `/metrics` lives on a dedicated port (9090).** The worker is queue-driven and otherwise has no HTTP surface. Adding `/metrics` is a small Kestrel-lite host inside the same process — see ADR-0012's plan §6 for the wiring.
- **`/metrics` is excluded from auth and from tracing.** Auth: dev bearer middleware skips `/metrics` paths (mirror of `/health/*`). Tracing: OTel sampling filter excludes `/metrics`. Prometheus scrapers should hit a no-auth, no-trace endpoint.

## Alternatives considered

- **OpenTelemetry Prometheus exporter (`OpenTelemetry.Exporter.Prometheus.AspNetCore`).** Rejected. Younger and less battle-tested than prometheus-net; its API has moved a couple times across recent OTel SDK versions. The single-SDK appeal is real but the operational cost of the API churn outweighs it for an exercise that needs to keep working.
- **Aspire's `AddServiceDefaults()` (which sets up OTel metrics with the Prometheus exporter).** Rejected — same reason as ADR-0011. Pulls Aspire packages we don't otherwise want.
- **App.Metrics.** Rejected. Older, less maintained, and the AspNetCore integration is less idiomatic than prometheus-net's.
- **Roll-your-own metric counters and expose a hand-written text endpoint.** Rejected. The exposition format has edge cases (escaping, label ordering, histogram bucket layout) that are easy to get subtly wrong. prometheus-net handles them.
- **Skip metrics entirely and rely on traces for everything.** Rejected. Traces are great for individual request debugging; metrics are how you find out something is wrong in the first place. The brief asks for both.
