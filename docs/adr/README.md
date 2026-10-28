# Decision records

Each record states one decision, the context that forced it, and the alternative we turned down and why.
Records are never edited after they are accepted; a changed decision gets a new record that supersedes
the old one.

| ADR | Decision | Status |
|---|---|---|
| [001](001-modular-monolith.md) | Modular monolith in three process roles | Accepted |
| 002 | MongoDB time-series collection for telemetry | Planned |
| [003](003-rabbitmq.md) | RabbitMQ with quorum queues and delay-queue retries | Accepted |
| 004 | Own small outbox and inbox on RabbitMQ.Client | Planned |
| 005 | Greedy cheapest-slot planner | Planned |
| 006 | The OCPP 1.6J messages smart charging needs | Planned |
| 007 | The agent changes devices only through confirmed plans | Planned |
| [008](008-gateway-command-routing.md) | Gateway command routing through per-replica queues | Accepted |

## Template

```markdown
# ADR-NNN: <decision, stated as a short sentence>

- **Status:** Proposed | Accepted | Superseded by ADR-NNN
- **Date:** YYYY-MM-DD
- **Decision ids:** D1–D9 (listed in AGENTS.md), if any

## Context
What forces the decision: requirements, constraints, numbers.

## Decision
What we do, in one or two paragraphs.

## Alternatives considered
Each alternative, and the specific reason it lost.

## Consequences
What becomes easier, what becomes harder, and what we must now test or watch.
```
