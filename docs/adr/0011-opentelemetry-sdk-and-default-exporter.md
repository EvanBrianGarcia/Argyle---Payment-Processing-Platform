# ADR-0011: OpenTelemetry SDK with console exporter default, OTLP exporter configurable

**Status:** Accepted
**Date:** 2026-06-06
**Phase:** 4

## Context

The brief asks for distributed tracing across the API and the settlement worker — specifically, a reviewer should be able to pull a single payment's full journey by `trace_id` and see the inbound HTTP request, the DB writes, the queue publish, the consume, the processor call, and the final state update as one connected story.

We need a tracing SDK in both `PaymentPlatform.Api` and `PaymentPlatform.Worker`, with auto-instrumentation for ASP.NET Core, HttpClient, Npgsql, and MassTransit, plus a small `ActivitySource` for handler-level spans that carry domain context (`payment_id`, `merchant_id`).

The exporter target is the open question. Pointing at a live OTel collector + Tempo/Jaeger in `docker-compose.yml` would add three services and meaningful setup friction for a reviewer who just wants to clone and run. The master plan §9 anchors the answer: "Wired (console exporter); OTLP exporter configurable."

## Decision

Use the OpenTelemetry .NET SDK in both hosts via `AddOpenTelemetry().WithTracing(...).WithMetrics(...)`. Auto-instrumentation packages:

- `OpenTelemetry.Instrumentation.AspNetCore` — inbound HTTP spans
- `OpenTelemetry.Instrumentation.Http` — outbound HttpClient spans
- `Npgsql.OpenTelemetry` — DB query spans
- `MassTransit.OpenTelemetry` — queue publish/consume spans with W3C `traceparent` propagation across the AMQP boundary

Plus one app-owned `ActivitySource("PaymentPlatform.Application")` used by handler-level spans (`CreatePayment.Handle`, `CapturePayment.Handle`, `SettlePaymentConsumer.Consume`). These spans tag `payment_id` and `merchant_id` so a trace search can pivot on domain identifiers, not just span names.

Default trace exporter is **console** — spans serialize to stdout as JSON and ride the existing log shipping path. The OTLP exporter (`OpenTelemetry.Exporter.OpenTelemetryProtocol`) is registered conditionally on `OpenTelemetry:Otlp:Endpoint` being set in configuration.

`/health/*` and `/metrics` are excluded from sampling via the AspNetCore instrumentation's `Filter` callback — they'd otherwise flood the span stream with noise.

Default sampler is `AlwaysOnSampler` in dev. The OTel `TraceIdRatioBasedSampler(0.1)` is documented as the production default but not shipped — sampling at 10% would make the demo walkthrough flake.

## Consequences

- **One SDK, one trace per request.** OTel manages `Activity.Current` end to end; Serilog's `TraceIdEnricher` reads the same id so log lines and spans share the trace id without manual plumbing. No double-bookkeeping.
- **Console exporter rides the same log pipeline as Serilog JSON output.** A reviewer running `docker compose logs api | jq` sees both structured logs and structured span data interleaved by timestamp. Phase 4's docs explain the `jq` filter to separate them.
- **OTLP exporter is one config flip away from production.** Setting `OPENTELEMETRY__OTLP__ENDPOINT=http://otel-collector:4317` in the environment is the entire production wiring step. The collector itself is the operator's choice (Tempo, Datadog, Honeycomb, X-Ray, etc.).
- **`/health/*` and `/metrics` are excluded from sampling.** Keeps the trace stream clean. Their work is observed via metrics + health-check responses, not spans.
- **AlwaysOnSampler in dev means every request gets a span.** Memory cost is bounded by the InMemoryExporter buffer size used in tests; the console exporter is fire-and-forget so prod memory cost is zero.
- **MassTransit's OTel instrumentation propagates `traceparent` across AMQP for free.** No envelope changes, no custom filter. The consume span's `ParentSpanId` is the publish span's `SpanId` automatically — an integration test asserts this rather than trusting the docs.

## Alternatives considered

- **Ship Jaeger or Tempo in `docker-compose.yml` as the default exporter target.** Rejected. Adds three services to the compose file and a UI a reviewer has to learn just to see "yes, the spans are real." Console exporter proves the instrumentation without the operational tax; OTLP is a one-line opt-in.
- **Hand-rolled `ActivitySource` everywhere instead of auto-instrumentation packages.** Rejected. Re-implementing ASP.NET Core / Npgsql / MassTransit span tagging by hand is the textbook example of work-not-worth-doing — the auto packages cover 90% of the surface with one registration line. Domain spans (which auto-instrumentation can't infer) use the app's single `ActivitySource`.
- **Use the OpenTelemetry SDK for both traces AND metrics (no prometheus-net).** Rejected. See ADR-0012 — the OTel Prometheus exporter is younger and less stable than prometheus-net.AspNetCore for the .NET surface. We split: OTel for traces, prometheus-net for metrics.
- **Aspire's service defaults (`AddServiceDefaults()`).** Rejected. Pulls Aspire-shaped packages we don't otherwise want. We cherry-pick the registration *shape* from Aspire's defaults but call the OTel APIs directly.
- **`TraceIdRatioBasedSampler(0.1)` as the dev default.** Rejected. The demo walkthrough relies on being able to find any single payment's trace; sampling at 10% would lose 9/10 traces and make "pull all logs by trace_id" land on empty results occasionally. Production sampling is documented; dev keeps `AlwaysOn`.
