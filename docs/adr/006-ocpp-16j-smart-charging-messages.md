# ADR-006: Use the OCPP 1.6J messages smart charging needs, and no more

- **Status:** Accepted
- **Date:** 2026-11-07
- **Decision ids:** D5

## Context

The gateway speaks OCPP 1.6J to chargers (the pinned Solidstudio simulator, and most wallboxes in the field).
A confirmed plan must start and stop charging in chosen 15-minute slots, and a client may limit a charger's
power directly. D5 puts per-slot `StartCharging`, `StopCharging` and `SetPowerLimit` commands in the core,
each with an idempotency key from `PlannedCommandKey` (plan, version, slot, type).

## Decision

- **Three central-system calls.**
  - `StartCharging` → `RemoteStartTransaction` on the configured connector with the configured `idTag`,
    plus a one-period `TxProfile` when the start carries a power.
  - `StopCharging` → `RemoteStopTransaction` for the charger's active transaction, which the gateway tracks
    from `StartTransaction`, from `MeterValues` naming a transaction (so a restarted gateway still knows it),
    and clears on the matching `StopTransaction`.
  - `SetPowerLimit` → `SetChargingProfile` in W: a `TxProfile` for the active transaction, otherwise a
    `TxDefaultProfile`, each with a fixed profile id so a new limit replaces the old one.
  Every payload validates against the official OCPP 1.6 schemas, embedded unchanged.
- **Replies map to the core's send result.** `Accepted` is accepted (the call's `uniqueId` is the vendor
  reference); `Rejected` and `NotSupported` are rejected; a `CallError` is rejected for `NotImplemented`,
  `NotSupported`, `FormationViolation`, `PropertyConstraintViolation` and `TypeConstraintViolation`, and
  transient otherwise. No answer within 30 s, or a closed socket, is transient.
- **Repeats do not act twice.** A start for a charger with an active transaction, or a stop for one without,
  is answered `already-in-effect` without a call, and the command is confirmed from the latest report.
- **Plans run under D5's per-slot keys, as runs.** Consecutive slots form one run: one start keyed by its first
  slot and one stop keyed by its last, so two adjacent cheap slots never get a stop and a start between them.
  A re-plan that continues the charging under way adds no start, otherwise it stops at the end of the
  current slot.

## Alternatives considered

- **OCPP 2.0.1.** Rejected: far larger to implement (device model, security profiles, new transaction
  handling), while 1.6J is still widely deployed and is what the simulator speaks. The adapter kit leaves
  room for a 2.0.1 provider later.
- **Composite schedules and `ClearChargingProfile`.** Rejected: the planner charges at the charger's maximum,
  so a plan never needs a power limit; one-period profiles cover the direct command.
- **A start and a stop per slot.** Rejected: it would interrupt charging between adjacent slots and double the
  commands without changing what the car receives.

## Consequences

- The pinned simulator accepts `SetChargingProfile` without changing its power, so a power limit below its
  simulated power is never confirmed and dead-letters after reconciling. Plans never use limits; the tests
  use a test charger whose answers they control; the README says so.
- Confirmation depends on the charger reporting `StatusNotification`; a charger that changes state silently is
  reconciled and resent after the timeout.
