# ADR-009: Trace with OpenTelemetry, and let messages keep the trace they were created in

- **Status:** Accepted
- **Date:** 2026-11-08
- **Decision ids:** D1, D3, D7

## Context

A command crosses three processes and a broker: the API stores it with an outbox entry, the worker's relay
publishes it, the dispatcher sends it (over HTTP to a vendor cloud, or through a gateway replica's queue to a
charger, D1), the gateway answers with a result, and the monitor confirms, resends or dead-letters it, sometimes
minutes later on a timer. When a charge does not start, an operator has to follow that path as one story.

Every message already carries a W3C `traceparent`, in the AMQP headers and in the body, and the two must match.
Every command stores the `traceparent` of the request that created it, and every message about the command reuses
it. Nothing yet started, continued or exported spans, and only a handful of vendor-call metrics existed.

Constraints: Core and Planning have no I/O and stay free of telemetry; tests run in parallel in one process and
need no collector; a dead collector must never slow a request, a frame or a command; secrets (API keys, vendor
tokens, charger passwords) never leave the process.

## Decision

- **OpenTelemetry in the Host only.** Messaging, the adapter kit and the Host create spans and instruments with
  `System.Diagnostics` (`ActivitySource`, `Meter`). Only `CheapHours.Host` references the OpenTelemetry SDK, through
  one `ObservabilitySetup` that every role calls: the resource (`cheaphours-{role}`, version, replica id), the
  CheapHours sources and meters, ASP.NET Core and HttpClient instrumentation, the runtime meter and JSON console
  logs with trace and span ids. Architecture tests keep it that way.
- **OTLP when configured, nothing otherwise.** Traces, metrics and logs go over OTLP only when
  `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Batch processors with bounded queues drop telemetry rather than block.
- **Messages keep their trace context.** Publish and handling spans are children of the context the message
  carries; the `traceparent` is never rewritten with the producer span's id. The monitor's later steps (reconcile,
  resend, re-announce, confirm, dead-letter) start their spans from the context stored on the command.
- **Inbound work starts its own trace.** Each OCPP frame, each poll of an account and each price-import run is a
  root span whose messages carry it; webhooks run in their request span. The `/ocpp` request, which lasts as long
  as the connection, and health probes are not traced. A telemetry batch is one span with a link to each message's
  trace.
- **Locally, the standalone Aspire dashboard** (one container in the compose file) receives what the roles export.

## Alternatives considered

- **Re-parent at each hop (header = the producer span), as the messaging conventions suggest.** Rejected: it
  changes what the envelope field means and breaks the rule that header and body match for re-published outbox
  entries; and the monitor, acting 40 seconds later, has nothing but the stored context to parent on anyway.
- **Jaeger for traces plus Prometheus and Grafana for metrics.** Rejected for local use: three containers and their
  configuration to see what one container shows for traces, metrics and logs together. The hosted backend is
  chosen with the deployment.
- **`Microsoft.Extensions.ServiceDefaults` from an Aspire app host.** Rejected: it assumes an AppHost project, which
  this repository does not have, and hides choices this design makes explicit.

## Consequences

- One trace shows a command from the API request to the device's confirmation, across the three roles, and a
  failed step is an error span with the exception type only.
- In the trace tree, a message's publish and handling spans are siblings under the context it carries instead of a
  producer→consumer chain; both carry `messaging.message.id`, so they still read in order.
- Span volume grows with the device fleet (every frame, heartbeats included, is a trace). Head sampling
  (`OTEL_TRACES_SAMPLER`) is left to the deployment and needs no code change.
- Metrics are recorded after the stored change, so they never count a transition that did not happen; a crash
  between the write and the record loses one sample, which is acceptable for metrics.
- The outbox lag is reported by the worker whose relay pass ran under the `outbox-relay` lease. That lease is taken
  per pass, so with several relaying workers each reports what its own latest pass measured.
- Tests observe spans and metrics in-process with an `ActivityListener` and a `MeterListener`, always filtered by
  trace id or by the host's meter factory, never by global counts.
