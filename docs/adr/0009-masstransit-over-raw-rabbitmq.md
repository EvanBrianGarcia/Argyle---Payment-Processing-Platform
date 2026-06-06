# ADR-0009: MassTransit over raw RabbitMQ.Client for publisher and consumer

**Status:** Accepted
**Date:** 2026-06-06
**Phase:** 3

## Context

Phase 3 adds a queue between the API (publisher) and the new Worker host (consumer). The transport is RabbitMQ — a fixed choice from master plan §15. The open question is whether to drive it via raw `RabbitMQ.Client` (`IConnection`/`IModel`) or via a higher-level .NET messaging library.

Hand-writing the transport on raw `RabbitMQ.Client` means we own: the consumer dispatch loop, the retry policy and DLQ topology, the trace propagation across the boundary, the consumer-side cancellation handling, and the test harness. None of this is novel work — every .NET messaging library does it the same way. We'd be writing infrastructure code that already exists, with worse documentation than the library version.

The shortlisted libraries: MassTransit, Wolverine, NServiceBus, EasyNetQ.

## Decision

Use **MassTransit v8** with the RabbitMQ transport.

- `PaymentPlatform.Api` registers the publisher via `services.AddMassTransit(cfg => cfg.UsingRabbitMq(...))`. The publisher implements `IOutboxPublisher` (an Application-layer abstraction) so the Application project stays transport-agnostic.
- `PaymentPlatform.Worker` registers the consumer via `services.AddMassTransit(cfg => cfg.AddConsumer<SettlePaymentConsumer>(); cfg.UsingRabbitMq(...))` with retry policy and DLQ binding on the receive endpoint.
- Message contracts live in `PaymentPlatform.Messaging` (a new shared assembly) with **no MassTransit dependency** so the contracts could be consumed from a non-MassTransit client later (e.g. a Python webhook delivery worker) without dragging MassTransit in.

## Consequences

- **We don't write the dispatch loop, the retry policy, the DLQ topology, the consumer cancellation, or the test harness.** All of these come from MassTransit and are battle-tested.
- **OpenTelemetry instrumentation is one line.** Phase 4 will add `.AddMassTransitInstrumentation()` to the OTel pipeline — no per-call manual span propagation needed.
- **`MassTransit.TestFramework`'s `ITestHarness` runs consumer tests against an in-memory bus.** Sub-second feedback loops for the consumer's logic, with the real RabbitMQ fixture reserved for the retry/DLQ tests where the transport behavior actually matters.
- **Two extra package dependencies in production (`MassTransit`, `MassTransit.RabbitMQ`) plus one in the test project (`MassTransit.TestFramework`).** All three are stable, BSL-licensed (free for our use), and on a regular release cadence.
- **Message routing is exchange-based.** The dispatcher uses `IPublishEndpoint` (MassTransit's exchange-publish primitive), not `ISendEndpoint` — settlement is conceptually a domain event ("this payment captured, please settle"), not a directed command, so publish is the right verb. The consumer's receive endpoint binds the `settlement` queue to the type-routed exchange.

## Alternatives considered

- **Raw `RabbitMQ.Client` (no library).** Rejected. ~300 lines of infrastructure code we'd write, badly, and revisit every time the protocol nuances changed (publisher confirms, mandatory routing, consumer cancellation, channel recovery). The library version is the textbook answer.
- **NServiceBus.** Rejected on licensing. Commercial license required for production use; the exercise is meant to be self-contained.
- **Wolverine.** Rejected on maturity. Promising and idiomatic but newer; the community footprint and the long-tail documentation are thinner. We'd hit unknowns; MassTransit is the known quantity.
- **EasyNetQ.** Rejected on scope. Thinner than MassTransit — no built-in retry/DLQ topology, no test harness equivalent to `ITestHarness`. We'd lose the two features that justify a library choice over raw `RabbitMQ.Client`.
- **`MassTransit.Mediator` for both sides (mediator-style in-process bus).** Rejected. The exercise is specifically about the cross-process queue boundary; an in-process mediator would skip the part of the system that needs to be exercised.
