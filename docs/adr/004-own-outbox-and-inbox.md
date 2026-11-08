# ADR-004: Keep a small outbox and inbox of our own on RabbitMQ.Client

- **Status:** Accepted
- **Date:** 2026-11-07
- **Decision ids:** D1, D5, D7

## Context

A command must never be lost between MongoDB and the broker, never be applied twice, and only count as
done when the device itself reports the new state. The worker, the broker and the charger can each die
mid-command. A library such as MassTransit offers an outbox, but v9 is commercial and v8's security patches
end in 2026. The telemetry pipeline already has its own inbox and a batching consumer with delay-queue
retries on `RabbitMQ.Client` (ADR-003).

## Decision

- **MongoDB is the source of truth; the broker carries wake-up calls.** Every write that creates or changes
  commands (a direct command, confirming or re-planning a plan, a resend, a re-announcement) adds the
  matching `CommandRequested` outbox entries in the same MongoDB transaction. Cancelling a replaced plan
  version's unsent commands deletes their unpublished entries in that transaction too.
- **One outbox relay under a lease.** On the worker, under the `outbox-relay` lease, the relay publishes due
  entries oldest first with publisher confirms and marks an entry published only after the broker confirms.
  It backs off from 1 s to 30 s while the broker is down; the entries wait. A crash between the confirm and
  the mark publishes the entry again.
- **The command's state machine is the inbox for command consumers.** The dispatcher acknowledges a message
  without a send when the command is not ready, and otherwise claims it with a conditional update, so a
  repeated message sends once. The gateway-result consumer drops a result its command can no longer take
  (`CanFire`). Every store write filters on the document version, so two writers never overwrite each other.
- **One command monitor instead of a boot-triggered reconcile.** Under the `command-monitor` lease it
  confirms sent commands from the device's reports (receipt time after the send), times them out and
  reconciles them (confirm, resend with the same key, or dead-letter), re-announces commands whose message
  was lost, and expires claims. It reconciles a rebooted charger, a lost message and a silent vendor the
  same way, within one tick of the timeout, so the gateway needs no reconcile of its own on
  `BootNotification`.
- **The OCPP route reaches the gateway without a new kit abstraction.** ADR-008 left open how the OCPP
  provider would publish to the gateway from the worker. It does not need to: the worker's gateway route
  publishes the `GatewayCommand` envelope itself to `gateway.{replicaId}.commands` (default exchange,
  `mandatory`, so a vanished queue is a transient failure), and the OCPP provider is used only inside the
  gateway, where the socket lives.

## Alternatives considered

- **MassTransit's outbox.** Rejected: licensing (above), and it would replace a consumer and retry design we
  already test.
- **Claims per outbox entry, many relays.** Rejected for now: one relay under a lease keeps the order per
  device for free, needs no claim expiry, and the demo's rate (a few commands a minute) is far below one
  relay's capacity.
- **Reconcile on `BootNotification`, as the System Design first proposed.** Rejected: it only covers a charger
  that reboots, not a lost message or a silent vendor, and it would be a second mechanism next to the
  timeout.
- **A separate inbox collection for commands.** Rejected: the command document already records every state
  it entered, and its transition table refuses repeats.

## Consequences

- No command is lost or sent twice when the worker or the broker stops mid-command; the fault tests in
  `tests/CheapHours.IntegrationTests/Faults` prove it, and `docs/reliability.md` maps every reliability row.
- Confirmation and dispatch can lag by up to one monitor interval (2 s); the next change measures time to
  confirmation.
- The relay and the monitor are single-active: a lease fails over within its duration, and commands wait in
  MongoDB meanwhile.
- **Scaling path:** give outbox entries claims partitioned by device and run several relays; split the
  monitor's passes by device range. Neither is needed at the planned load (ADR-001's "simple until
  measured").
- Replica command queues are quorum queues with `x-expires` (10 minutes without a consumer), which the pinned
  broker accepts; a dead replica's queue disappears and its commands are recovered by reconciliation.
