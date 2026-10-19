# ADR-001: Build CheapHours as a modular monolith run in three process roles

- **Status:** Accepted
- **Date:** 2026-10-16

## Context

CheapHours has three kinds of work with very different runtime needs:

- the **public API** serves people and the AI assistant, and must stay fast whatever devices are doing;
- the **device gateway** holds long-lived OCPP WebSockets, so a restart drops every charger connection and it must be deployed rarely and shut down gracefully;
- the **worker** runs scheduled jobs (price import, polling) and message consumers, and talks to slow vendor APIs.

The system is built and run by one person, at a target load of about 1,000 devices reporting every 30
seconds (roughly 33 readings a second). The extensibility goal is that a new provider needs no change to
core modules.

## Decision

One codebase, built into **one container image**, started in one of three roles by
`CheapHours.Host --role api | gateway | worker`. The roles share MongoDB and RabbitMQ.

Inside the codebase, modules are separate projects with enforced boundaries:

- `CheapHours.Core` holds the domain types and references nothing else, with no I/O;
- each provider is its own project and may reference only Core and the adapter kit;
- message contracts live in `CheapHours.Messaging.Contracts`, which references only Core.

Architecture tests (`tests/CheapHours.ArchitectureTests`) check these rules at two levels, project
references and type dependencies, so a violation fails the build rather than waiting for code review.

## Alternatives considered

- **A microservice per provider, plus API, planner and dispatcher services.** Rejected: it means more to
  run, deploy and observe, with no load or team-size reason to pay that cost. The boundaries that would
  make a later split possible already exist and are tested.
- **A single process doing everything.** Rejected: a gateway restart would also restart the API, and slow
  vendor calls would share threads and failure modes with user requests.
- **Separate images per role.** Rejected for now: one image keeps the build, the vulnerability scan and
  versioning simple, and the roles differ only in what they start.

## Consequences

- One build, one image, one version. Kubernetes runs three Deployments from the same image with different arguments.
- The API scales horizontally on its own. The gateway needs connection-aware command routing once it has more than one replica (ADR-008).
- Scheduled jobs must run on exactly one worker replica, chosen with a lock in MongoDB.
- Module boundaries depend on the architecture tests staying strict; weakening a rule needs a new ADR.
- Splitting a module into its own service later means changing deployment, not rewriting the domain.
