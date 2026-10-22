# AGENTS.md

How AI coding agents (Claude Code, Codex, others) work in this repo, and what is built so far.
**Agents keep the "Status" section current:** when something lands, move it from "Left to build" to
"Done". Describe features, not phases or dates.

## Project

CheapHours is a .NET 10 backend that connects energy devices from different vendors (OCPP wallbox,
polled car cloud, webhook heat-pump cloud) behind one device model, imports German day-ahead prices,
and charges an EV in the cheapest 15-minute slots before departure, with commands that are never lost
or applied twice. Scope is deliberately small: the "Deliberately leave out" list in the Project Idea
page is binding.

| Source | What it holds |
|---|---|
| Notion: *CheapHours: System Design* | Architecture, data model, flows, reliability table, API surface |
| Notion: *CheapHours: Project Idea & Positioning* | Scope, what to leave out, the "done when" checklists |
| `docs/plan.md` | Build plan, decisions D1–D9, API conventions |
| `openspec/` | Specs and changes (local only, git-ignored): `changes/` holds the active change, `specs/` the accepted specs |
| `docs/adr/` | Decision records |

## Status

**Next:** the OCPP gateway (`/opsx:propose add-ocpp-gateway`), then stop for review.

### Done

- **Solution and build:** 11 source and 3 test projects in `CheapHours.slnx`; one host with
  `--role api | gateway | worker`; central package versions; warnings are errors.
- **Module boundaries:** architecture tests keep Core free of other modules, I/O and vendor names, keep
  providers apart, and forbid direct clock reads in Core and Planning.
- **Domain model:** prefixed typed ids (`dev_`, `goal_`, `plan_`, `cmd_`, …), `Capabilities`,
  `DeviceReading`, `Device` with typed attributes, `DeviceCommand`, closed `SendResult` and
  `DeviceEvent`, `CommandState`, `Goal`, `Plan` and `PlanSlot` on the 15-minute grid.
- **Adapter kit interfaces:** `IDeviceProvider`, `IPollingProvider`, `IInboundTranslator`,
  `ICommandSender`, `IWebhookVerifier`; `AddProvider<T>` registration and a startup validator.
- **Message contracts:** `Message` envelope (id, time, type, schema version, `traceparent`), seven
  messages, shared JSON options, per-replica gateway routing key `gateway.{replicaId}.commands`.
- **Public API contract:** the ten `/v1` endpoints behind API-key auth (they answer 501 until
  implemented), Problem Details from `ErrorCatalogue`, the OpenAPI snapshot `docs/api/openapi.v1.json`
  with a drift test, Scalar UI in Development.
- **Command lifecycle (Core, no I/O):** `Command` with a fixed transition table and history,
  deterministic planned-command keys, retry policy with jittered backoff and `Retry-After`, reconcile
  decision (confirm, resend or fail) against the device's last observation. Property tests (FsCheck)
  cover terminal states and key uniqueness.
- **ADRs:** 001 (modular monolith), 008 (gateway command routing).
- **Tests:** 309 green (unit, property, architecture, API contract).

### Left to build

- **Local environment:** `docker compose` with MongoDB (single-node replica set), RabbitMQ and an OCPP
  charge-point simulator.
- **OCPP gateway:** `/ocpp/{chargePointId}` WebSocket (`ocpp1.6`, basic auth), OCPP-J frame codec with
  schema validation, `BootNotification`/`Heartbeat`/`StatusNotification`, connection registry, offline
  after missed heartbeats, graceful shutdown, health endpoints.
- **Telemetry pipeline:** MongoDB indexes and time-series `telemetry` with TTL, `MeterValues` to readings,
  RabbitMQ topology with DLQ and retries, inbox and batched consumer, device and telemetry endpoints,
  API keys stored in MongoDB.
- **Cloud providers:** kit resilience (per-provider `HttpClient` pipeline, token store, rate limiting with
  `Retry-After`), shared provider contract suite, car-cloud simulator and polling provider, heat-pump
  simulator and webhook provider, polling scheduler with a leader lock, bulkhead test, OCPP on the suite.
- **Prices and planner:** Energy-Charts price import with retries and fallback, greedy planner with
  property tests, re-planning on new prices, goal change or SoC drift, goal/plan/prices/savings
  endpoints, ADR-002 and ADR-005.
- **Command dispatch and reliability:** transactional outbox and relay, dispatcher (OCPP via the gateway
  queue), confirmation timeout → reconcile → resend wired to the lifecycle, plan-confirm and command
  endpoints with idempotency middleware, fault tests for every reliability-table row, OpenTelemetry
  traces and metrics, ADR-003, ADR-004 and ADR-006.
- **Delivery:** small dashboard on the public API, GitHub Actions pipeline, Helm chart, k6 load test,
  README with architecture diagram and demo.
- **CheapHours Assistant (separate repo):** MCP server and agent host over the public API only.

### Open items

- Waiting for the user's review: the database ERD, and the owners-and-auth proposal (`chargePoints`
  collection, another owner's id answers 404, `apiKeys` gain `createdAt`/`revokedAt`).
- D8: rename "WattBridge" to CheapHours in Notion (user).
- D9: needs the week-by-week roadmap from the user.

### Decisions (from `docs/plan.md`)

| # | Topic | Status |
|---|---|---|
| D1 | Worker → charger via per-replica gateway queue | Accepted: [ADR-008](docs/adr/008-gateway-command-routing.md); routing key in `GatewayCommand` |
| D2 | Goal has vehicle + charger; typed device attributes | Accepted: `Goal`, `DeviceAttributes` |
| D3 | `apiKeys` + `ownerId`; one seeded demo user | Accepted: shape and in-memory key store built; MongoDB storage comes with the telemetry pipeline |
| D4 | 15-minute slots everywhere | Accepted: `PlanSlot` |
| D5 | Per-slot Start/Stop/SetPowerLimit in core | Accepted: `CommandType` |
| D6 | `IWebhookVerifier` per provider | Accepted: interface built; first implementation with the heat-pump provider |
| D7 | Command states `Pending, Published, Sent, Confirmed, Reconciling, Rejected, DeadLettered` | Confirmed by the user against the Notion diagram |
| D8 | Rename "WattBridge" to CheapHours in Notion | Open (Notion edit, user) |
| D9 | Map the plan onto the week-by-week roadmap | Needs the roadmap from the user |

### Package versions

Pinned in `Directory.Packages.props`. `Microsoft.*` 10.0.x packages match the installed runtime patch
(10.0.12).

| Package | Version | Used by |
|---|---|---|
| Microsoft.AspNetCore.OpenApi | 10.0.12 | Host (API role) |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.12 | Providers.Kit |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.12 | Providers.Kit |
| Scalar.AspNetCore | 2.18.0 | Host (Development only) |
| FsCheck.Xunit.v3 | 3.5.0 | UnitTests (property tests) |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | ApiTests |
| Microsoft.Extensions.TimeProvider.Testing | 10.11.0 | UnitTests |
| TngTech.ArchUnitNET.xUnitV3 | 0.13.4 | ArchitectureTests |
| xunit.v3 | 4.0.2 | all tests |

Tests run on **Microsoft.Testing.Platform** (opted in via `global.json`): the .NET 10 SDK no longer
runs xUnit v3 through VSTest, so `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are not used.

## Workflow

Spec-driven, one OpenSpec change per feature, with a human review between each step:

1. `/opsx:propose`: write proposal, specs, design and tasks. **Stop for review.**
2. `/opsx:apply`: implement tasks in order; tick each in `tasks.md` and update Status above.
3. Gate: `dotnet build && dotnet test` green plus the change's own acceptance checks. **Stop for review.**
4. `/opsx:archive`: specs move into `openspec/specs/`; move the feature to "Done" above.

If a decision changes during implementation, update the spec and design first (`/opsx:update`),
then the code. `openspec/` stays out of git.

## Commands

```
dotnet build                                   # must report 0 warnings
dotnet test                                    # unit, architecture, API contract tests
dotnet test --project tests/CheapHours.UnitTests --filter-class "*RoleParserTests"   # one class (MTP/xUnit v3 syntax)
dotnet test --filter-method "*Role*"           # by method name; VSTest-style --filter is not supported
dotnet run --project src/CheapHours.Host -- --role api      # or gateway | worker
UPDATE_SNAPSHOT=1 dotnet test --project tests/CheapHours.ApiTests   # deliberately refresh docs/api/openapi.v1.json
openspec validate --strict                     # check all changes and specs
openspec status --change <name>                # artifact progress
```

## Repo layout

```
src/CheapHours.Core                  domain types; references nothing, no I/O
src/CheapHours.Messaging.Contracts   message records + shared JSON options; references Core only
src/CheapHours.Planning              planner (not built yet)
src/CheapHours.Persistence           MongoDB repositories (not built yet)
src/CheapHours.Messaging             outbox, inbox, RabbitMQ (not built yet)
src/CheapHours.Providers.Kit         adapter interfaces, registration, resilience
src/CheapHours.Providers.Kit.Testing shared provider contract suite (not built yet)
src/CheapHours.Providers.{Ocpp,CarCloud,HeatPumpCloud}
src/CheapHours.Host                  single entry point: --role api | gateway | worker
tests/                               UnitTests, ArchitectureTests, ApiTests (+ later Integration, Contract, EndToEnd)
docs/api/openapi.v1.json             committed public contract
docs/adr/                            decision records
openspec/                            specs and changes (local only, git-ignored)
```

## Conventions

```csharp
// Typed, prefixed ids; never pass raw strings across module boundaries.
public readonly record struct DeviceId
{
    public const string Prefix = "dev_";
    public string Value { get; }
    private DeviceId(string value) => Value = value;
    public static DeviceId Parse(string s) =>
        TryParse(s, out var id) ? id : throw new FormatException($"Expected prefix '{Prefix}'.");
    public static bool TryParse(string? s, out DeviceId id) { /* prefix + non-empty body */ }
    public override string ToString() => Value;
}

// Units in names, UTC only, invariants checked in the constructor, get-only properties (so `with`
// cannot bypass them). Providers key readings by the vendor's id; the core maps that to a DeviceId.
public sealed record DeviceReading
{
    public DeviceReading(string externalId, DateTimeOffset ts, int powerW, long energyWh, decimal? socPercent = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(energyWh);
        ExternalId = Guard.NotBlank(externalId);
        Ts = Guard.Utc(ts);
        // ...
    }

    public string ExternalId { get; }
    public DateTimeOffset Ts { get; }
}
```

- C# file-scoped namespaces, one public type per file, `sealed` by default, records for data.
- Inject `TimeProvider`; never `DateTime.Now`/`UtcNow` in Core or Planning (an architecture test enforces this).
- All times are `DateTimeOffset` in UTC; convert to Europe/Berlin only for display.
- Validate at the edges only (API handlers, gateway frames, webhooks, vendor responses, config).
  Domain types throw on invariant violations because, past the edge, a violation is a bug.
- Vendor failures are `SendResult.Rejected`/`Transient`, never exceptions.
- JSON: camelCase fields, PascalCase enum strings, units in field names, ISO 8601 UTC with `Z`.
- Errors: Problem Details from `ErrorCatalogue` only; never hand-write a type URI.
- Slot maths never assumes 96 slots a day.
- Commands change state only through `Command`'s trigger methods (table: `CommandTransitions.Table`);
  use `CanFire` to drop duplicate or late events instead of catching.
- Planned-command keys come only from `PlannedCommandKey.For`:
  `{planId}:v{version}:slot_{yyyy-MM-ddTHH:mmZ}:{CommandType}`, e.g.
  `plan_8812:v3:slot_2026-10-12T01:00Z:StartCharging`. A resend keeps the key.
- Retry defaults (`RetryPolicyOptions`): 6 attempts, 5 s base delay doubling to a 2 min cap, full jitter,
  vendor `Retry-After` as a floor, 60 s confirmation timeout. Inject `Random` (seeded in tests).

## Testing

- Unit (xUnit, `FakeTimeProvider`) and property-based (FsCheck) for planner and state machine, written test-first.
- Architecture (ArchUnitNET) for module boundaries; each rule is proven by planting a violation once.
- API contract: snapshot of `/openapi/v1.json` plus behaviour tests via `WebApplicationFactory`.
- Integration (Testcontainers, real MongoDB and RabbitMQ) once persistence and messaging land; provider
  contract suite (WireMock.Net) with the cloud providers.
- Never mock our own interfaces in integration tests; use real containers or faithful fakes.
- Failure paths first: every row of the System Design reliability table gets a test.

## Boundaries

**Always**
- Run `dotnet build` and `dotnet test` before marking a task done; paste failures verbatim, never summarise them away.
- Update the Status section and tick `tasks.md` as work lands.
- Keep the spec ahead of the code: change the spec or design first, then implement.
- Keep secrets, tokens and API keys out of logs, errors, tests and commits.

**Ask first**
- Adding a package not named in the active design.
- Changing a public contract: `/v1` DTOs, the OpenAPI snapshot, message schemas, adapter interfaces.
- Changing a decision D1–D9 or an accepted ADR.
- Committing, pushing, creating branches or PRs. Commit messages are one compact line that says only
  what the commit completes (no phase, milestone or task numbers) and carry no co-author trailer.
- Anything on the "Deliberately leave out" list.

**Never**
- Reference another module from `CheapHours.Core`, or one provider from another.
- Name a vendor in Core, Planning, Messaging or Persistence.
- Delete or skip a failing test to get green.
- Make a breaking change inside `/v1`.
- Start the next change before the user approves the current one's gate.
- Commit anything under `openspec/`.
