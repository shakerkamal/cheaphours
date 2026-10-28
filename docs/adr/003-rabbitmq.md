# ADR-003: Use RabbitMQ with quorum queues and delay-queue retries

- **Status:** Accepted
- **Date:** 2026-10-26
- **Decision ids:** D1 (per-replica gateway queues build on this topology)

## Context

The gateway, worker and later the API exchange messages:
- telemetry now;
- commands and their results with command dispatch.

The reliability table requires that:
- a message is never silently lost;
- a message that keeps failing is retried three times with backoff, then parked with a reason;
- a broker restart loses nothing;
- one bad message never holds back others.

Delivery is at-least-once, so consumers deduplicate, and they need no replay of old history.

## Decision

- **Broker:** RabbitMQ 4, through `RabbitMQ.Client` 7 and our own small wrapper (connection, publisher,
  batching consumer).
- **Topology, declared from code at startup:**
  - one topic exchange, `cheaphours.events`, with the message type as the routing key;
  - per consumer, a durable **quorum** queue (`worker.telemetry`), three **delay queues** (5 s, 30 s, 2 min)
    that dead-letter back into it, and a dead-letter queue (`worker.telemetry.dlq`).
  - A queue that exists with other arguments stops startup.
- **Publishing:** every publish is persistent and `mandatory`, and waits for the publisher confirmation
  (5 s timeout), so an unconfirmed message is an error the caller sees. For telemetry, the charger then
  gets `InternalError` and resends.
- **Failures:**
  - The consumer handles batches. A failing batch is split, and a message that still fails is copied,
    confirmed, to its next delay queue and then acknowledged.
  - After the fourth attempt it goes to the dead-letter queue with its attempts and a category. Unreadable
    bodies and newer schema versions go there at once.
  - A database outage keeps the batch unacknowledged and retries it with backoff, spending no attempts.
- **Safety net:** the main queue's `x-delivery-limit` of 20 and a dead-letter target park a message that
  keeps crashing its consumer. Without a target, RabbitMQ 4 would drop it.

## Alternatives considered

- **Kafka.** Rejected: replay is not needed, it is heavier to run, and per-message retry and
  dead-lettering would have to be built on top.
- **Classic queues.** Rejected: mirrored classic queues are gone in RabbitMQ 4, and quorum queues are the
  durable, replicated default.
- **One retry queue with per-message TTL.** Rejected: messages expire only at the head, so a 2-minute retry
  would hold back a 5-second one behind it.
- **The delayed-message exchange plugin.** Rejected: it is not in the stock image, and its delayed messages
  are not replicated.
- **Immediate requeue with `nack`.** Rejected: no backoff, and every requeue counts against the delivery
  limit.
- **MassTransit.** Rejected: version 9 is commercial (see ADR-004, outbox and inbox).

## Consequences

- **The pattern is reusable.** Every new consumer gets the same retries and dead letters by adding its
  queues to the topology. The gateway command queues (ADR-008) are next.
- **More queues.** There are five per consumer; names carry a configurable prefix, so parallel test runs
  never share them.
- **The broker becomes a hard dependency for meter values.** Gateway readiness now includes the broker.
- **Tests to keep:** declaring twice, a conflicting queue, four timed attempts then the dead-letter queue,
  the delivery limit, a database outage, a broker restart, and a publish while the broker is down.
