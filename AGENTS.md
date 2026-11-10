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
| `docs/plan.md` | Build plan, decisions D1–D9, API conventions (local only, git-ignored) |
| `openspec/` | Specs and changes (local only, git-ignored): `changes/` holds the active change, `specs/` the accepted specs |
| `docs/adr/` | Decision records |

## Status

**Next:** the CI pipeline (`add-ci-pipeline`) is built and its local gate is green; waiting for the user's review,
then a pull request whose run is the pipeline's first proof, before `/opsx:archive`.

### Done

- **Solution and build:** 11 source projects, 2 simulators and 5 test projects in `CheapHours.slnx`; one
  host with `--role api | gateway | worker`; central package versions; warnings are errors.
- **Module boundaries:** architecture tests keep Core free of other modules, I/O and vendor names, keep
  providers apart, forbid direct clock reads in Core and Planning, and keep simulators free of any `src`
  reference (and in the solution).
- **Domain model:** prefixed typed ids (`dev_`, `goal_`, `plan_`, `cmd_`, …), `Capabilities`,
  `DeviceReading`, `Device` with typed attributes, `DeviceCommand`, closed `SendResult` and
  `DeviceEvent` (including `Discovered`, which carries the type and the attributes the type requires),
  `CommandState`, `Goal`, `Plan` and `PlanSlot` on the 15-minute grid.
  - `Plan` also carries target-missed, prices-complete and when it was made; a draft must expire after
    it was made, and `StatusAsOf(now)` shows a draft past its expiry as `Expired`. `PlanReason`
    (`Created`, `GoalChanged`, `PricesPublished`, `ChargeDrift`).
  - `PriceSlot`: one zone's price for one 15-minute UTC slot, negative prices allowed.
  - One device model: `Device` carries its last known `DeviceState` (status, last seen, report interval,
    hardware, last reading) and `StatusAsOf(now)` shows a device silent for more than three report
    intervals as `Offline`.
  - `SlotGrid` (floor, ceiling, alignment) and `TelemetryBucket` (energy per 15-minute slot, average
    power, last SoC).
- **Adapter kit interfaces:** `IDeviceProvider`, `IPollingProvider`, `IInboundTranslator`,
  `ICommandSender`, `IWebhookVerifier`; `AddProvider<T>` registration and a startup validator.
- **Adapter kit machinery (what every HTTP provider gets from `AddProvider<TProvider, TOptions>`):**
  - Its own named `HttpClient` and resilience pipeline: total timeout, jittered retry for 5xx, 408,
    timeouts and connection failures (never 429 or other 4xx), circuit breaker, attempt timeout. One
    pipeline per provider is the bulkhead.
  - `ProviderHttpClient<TProvider>`: one call that rate-limits per account (a token bucket on the
    injected clock, waiting at most 2 s), attaches the OAuth bearer token, and after a 401 refreshes once
    and retries once.
  - One failure classification (`VendorFailure`): transient with `Retry-After` (seconds or HTTP date,
    capped at 1 h), rejected with a sanitised vendor code (bodies are never copied), unknown device,
    reconnect-needed; `ToSendResult`/`ToPollFailed`.
  - OAuth 2.0 tokens encrypted with Data Protection, cached, refreshed 60 s before expiry, once per
    account at a time; a rotated refresh token is stored compare-and-set before use; `invalid_grant`
    re-reads the store before marking the account reconnect-needed. Tokens and secrets are never logged.
  - Accounts seeded from configuration without overwriting rotated tokens; a reconnect-needed account
    is linked again by the next start.
  - `VendorUnits` (kW→W, kWh→Wh, UTC-only timestamps), `ProviderPollingSettings`, and a
    `CheapHours.Providers` meter tagged with provider and outcome only.
  - Options validated at startup; errors name the key and never contain a value.
- **Adapter kit for webhook providers (`AddProvider<TProvider, TOptions>` with `WebhookProviderOptions`):**
  signing secret (at least 32 characters), timestamp tolerance (1 s – 15 min, default 5 min), expected
  report interval and linked accounts (owner, vendor account id), validated without echoing values;
  `WebhookAccountDirectory` looks up an account's owner and the provider's report interval. Startup
  fails when an `IWebhookVerifier` is not also an `IInboundTranslator` declaring `PushesEvents`.
- **Shared provider contract suite (`Providers.Kit.Testing`):** `ProviderContractTests<TProvider>` with a
  harness per provider and `WireMockVendorFake` (OAuth token endpoint with rotation and revocation,
  failure and delay injection, request log). One fact per rule: normalised readings, removed devices,
  idempotency keys, 401 refresh, revocation, 429/5xx/timeouts transient, validation rejected,
  undeclared capabilities refused before any call, no vendor failure thrown, pure and total translation,
  and webhook verification (a signed sample verifies; a tampered body, a wrong secret, a stale timestamp
  and missing or garbled headers are refused without an exception).
  Rules that do not fit a provider's interaction style are skipped with a reason. CarCloud passes every
  polling, OAuth and command rule; OCPP passes translation and normalisation; HeatPumpCloud passes
  translation, normalisation and verification; three deliberately broken providers fail it (one throws
  on a vendor 500, one sends an undeclared capability, one accepts every signature).
- **Car-cloud provider (`carcloud`):** polls each linked account (vehicles, then each vehicle's state):
  `Discovered` with the battery capacity, `Connected` with make and model, status (offline when asleep),
  readings with session energy as the meter, `DeviceRemoved` on a 404, `MalformedInput` for a timestamp
  without an offset; 429 or an open circuit fail the whole poll, a 5xx on one vehicle skips it. Start
  and stop pass the idempotency key; `SetPowerLimit` is refused before any call. README in the project.
- **Car-cloud simulator (`simulators/CheapHours.Simulators.CarCloud`):** rotating OAuth tokens with
  expiry, a per-account rate limit with `Retry-After`, vehicles whose charge rises with the clock, start
  and stop deduplicated by `Idempotency-Key`, admin endpoints (plug in, unplug, start, revoke), in-memory
  state; runs in-process in tests and as the compose service `car-cloud`. A conformance test runs the
  CarCloud contract facts against it.
- **Heat-pump-cloud provider (`heatpumpcloud`, the first `IWebhookVerifier`, D6):** verifies
  `HeatPump-Signature: t=<unix seconds>,v1=<hex>` (HMAC-SHA256 over `"<t>.<raw body>"`, constant-time,
  within the tolerance of receipt, never throws); translates `device.state` into `Discovered`
  (`HeatPump`), `Connected` with maker and model, a status (`Heating`, `HotWater`, `Defrost`, `Standby`
  → `Available`; `Fault` → `Faulted`; other modes `Unknown`; offline → `Offline` with no reading) and a
  reading with the lifetime meter; `device.deleted` into `DeviceRemoved`; other types into nothing;
  unreadable bodies or a timestamp without an offset into one `MalformedInput`. Registered in the gateway
  only when `Providers:HeatPumpCloud` exists. README in the project.
- **Heat-pump-cloud simulator (`simulators/CheapHours.Simulators.HeatPumpCloud`):** posts a signed
  `device.state` per heat pump every interval (default 15 s) to its callback URL; power follows the mode
  and lifetime energy grows with the injected clock; a failed delivery (5xx, 408, 429, timeout,
  connection error) is retried after 1, 2, 4 and 8 s, re-signed over the same body, up to five attempts;
  admin endpoints redeliver the last event, change the mode, remove a heat pump (`device.deleted`) and
  list deliveries; `/health`. Runs in-process in tests and as the compose service `heat-pump-cloud`. A
  conformance test checks that the provider verifies and translates what it sends.
- **Webhook ingestion (gateway role):** `POST /webhooks/{providerId}/{externalAccountId}` checks, in this
  order: readiness (503 while starting or draining), a registered verifier (404), a 64 KB body cap (413),
  the signature over the raw body (401, before any parsing and before the account is looked up), the
  configured account (404 and a warning), translation (400 for a body the provider cannot read), then
  records through the shared `DeviceEventIngestor` (a discovered device joins the account's owner with
  the provider's report interval) and answers 202 only after the readings are published; a broker or
  database outage gives 503 so the vendor retries. A redelivered webhook gets the same telemetry message
  id, so the inbox stores it once. Logs never carry the body or headers.
- **Vendor-cloud polling (worker role):**
  - A MongoDB lease (`locks`) picks one leader; it renews, stops its loops when the lease is lost, and
    releases it on shutdown.
  - One loop per polling provider with a cap on concurrent polls, a poll deadline, and the schedule stored
    with the account (`nextPollAt`; `pausedUntil` from the vendor's `Retry-After`, so a new leader honours
    it). A throwing provider is scheduled as a failure. Reconnect-needed accounts are skipped.
  - Poll results go through the shared `DeviceEventIngestor` (`Host/Devices`, also used for webhooks) and
    `DeviceEventRecorder`: a discovered device joins the account's owner with the poll interval as its
    report interval (presence on read works for vehicles); readings get a message id derived from the
    provider, the vendor id and the readings' times (UUIDv8), so a repeated poll adds no duplicate; a
    removal marks the device `Offline` with `removedAt` and keeps it.
  - CarCloud is registered only when `Providers:CarCloud` exists; Data Protection uses the default key
    location or `DataProtection:KeysPath`.
- **Message contracts:** `Message` envelope (id, time, type, schema version, `traceparent`), seven
  messages, shared JSON options, per-replica gateway routing key `gateway.{replicaId}.commands`.
- **Public API contract:** the twelve `/v1` endpoints behind API-key auth (they answer 501 until
  implemented), Problem Details from `ErrorCatalogue`, the OpenAPI snapshot `docs/api/openapi.v1.json`
  with a drift test, Scalar UI in Development. `PATCH /v1/goals/{id}` (scope `read`) changes a goal's
  target or departure; `PlanDto` carries `pricesComplete`; `GET /v1/goals` (scope `read`) lists goals with
  `GoalSummaryDto` and `PlanSummaryDto`.
- **Command lifecycle (Core, no I/O):** `Command` with a fixed transition table and history,
  deterministic planned-command keys, retry policy with jittered backoff and `Retry-After`, reconcile
  decision (confirm, resend or fail) against the device's last observation. Property tests (FsCheck)
  cover terminal states and key uniqueness.
  - Eight states (D7): `Cancelled` is terminal, reached by `Cancel` from `Pending` or `Published` with a
    reason, and never counts as an attempt.
  - A command carries the time it is due (UTC, defaulting to its creation). `Command.Restore` rebuilds a
    stored command by replaying its history through the table and refuses one the table cannot produce.
  - `CommandConfirmation.IsConfirmed`: a report received after the send that shows the requested state
    (the same rule the reconcile decider confirms by); with `CommandVendorReferences.AlreadyInEffect`, an
    older matching report counts too.
- **Local environment:** `docker compose` with MongoDB 8.0 (single-node replica set, self-initialising),
  RabbitMQ 4.2, the Solidstudio OCPP 1.6 simulator built from a pinned commit, the car-cloud
  simulator (`5090`) and the heat-pump-cloud simulator (`5091`, posting to the gateway through
  `host.docker.internal`), on pinned .NET images, and the owner dashboard's web server `web`
  (`nginx:1.30.5-alpine`, `127.0.0.1:5070`, `dashboard/site` and `docker/web/nginx.conf` mounted read-only,
  `/v1` forwarded to the API on `host.docker.internal:5080`, CSP / `Referrer-Policy` / `nosniff` on every
  response, 502 while the API is stopped); six healthy services. The standalone Aspire dashboard
  (`aspire-dashboard:13.6.1`, UI `localhost:18888`, OTLP `4317`/`4318`, loopback only, no login, no health check)
  receives what the roles export when started with `OTEL_EXPORTER_OTLP_ENDPOINT` (README "Observe a command").
  - Local secrets live in a git-ignored `.env` (the charger password, the RabbitMQ account, the
    car-cloud client secret and seed refresh token, the heat-pump signing secret).
  - The gateway's password hash and `Providers:HeatPumpCloud` settings, the broker connection string
    and the worker's `Providers:CarCloud` settings live in `dotnet user-secrets`.
  - The simulator sends meter values every 15 s during a charging session started with its admin
    scripts (README "Run locally").
- **OCPP gateway (gateway role):**
  - `/ocpp/{chargePointId}` WebSocket: basic auth against `chargePoints` (SHA-256, constant-time) and the
    `ocpp1.6` subprotocol, both checked before the upgrade; registrations seeded from configuration.
  - OCPP-J codec and validation against the official OCPP 1.6 schemas (embedded unchanged,
    CC BY-ND 4.0, hash-checked); malformed or invalid frames get a `CallError`, never a dropped socket.
  - `BootNotification`, `Heartbeat`, `StatusNotification`, `MeterValues`, `StartTransaction` (any
    `idTag`; the transaction id is the seconds since 2026-01-01Z) and `StopTransaction`; other actions
    get `NotSupported`/`NotImplemented`.
  - One translation path: `OcppProvider.Handle` gives the reply plus normalised `DeviceEvent`s, and
    `Translate` gives the same events. A vendor-neutral `DeviceEventRecorder` stores hardware and status,
    and publishes all of a frame's readings as one confirmed `TelemetryReceived` before the charger is
    answered. A broker failure gives `InternalError`.
  - Meter values: the energy import register (the default measurand), active import power and SoC;
    phase-less `Raw` samples only; converted to Wh, W and %; untrusted future timestamps are replaced by
    the receipt time.
  - Each registration carries the charger's `maxPowerW` (D2).
  - A `devices` document per charger, created when its connection is accepted (a reconnecting charger
    does not boot again); status and `lastSeenAt` only move forward (charger reports compare
    with the last report, gateway-set `Unknown`/`Offline` with the current status).
  - Connection registry `chargePointId → replicaId` (D1) with conditional removal; a newer connection
    replaces an older one (1008).
  - Offline after three missed heartbeat intervals (`DevicePresence` in Core, a monitor on `TimeProvider`).
  - Graceful shutdown (1001, registry sweep, devices not flapped to offline); `/health/live`,
    `/health/ready` (MongoDB and RabbitMQ reachable, not draining).
  - MongoDB and broker setup retry in the background, so a slow dependency delays readiness instead of
    crashing the gateway.
  - Commands (ADR-008): once initialised, each replica declares and consumes `gateway.{replicaId}.commands`
    (quorum, `x-expires` 10 min, prefetch 16) and stops consuming when draining; a broker outage is waited
    out. A start for a charger with an active transaction, or a stop for one without, is answered
    `already-in-effect` without a call; otherwise `OcppCommandCalls` builds `RemoteStartTransaction`,
    `RemoteStopTransaction` or `SetChargingProfile` (W, `TxProfile` or `TxDefaultProfile`; payloads checked
    against the official schemas, now embedded too), `ChargerConnection.CallAsync` waits for the reply by
    `uniqueId` (timeout 30 s; a closed socket fails it), and one `GatewayCommandResult` is published before
    the ack (three failed publishes requeue). Options `Gateway:Commands`.
  - The active transaction (`devices.activeSessionId`) is set by an accepted `StartTransaction` or a
    `MeterValues` naming one, cleared by the matching `StopTransaction`, stored before the reply and kept
    across a gateway restart.
- **Command dispatch (worker, ADR-004):**
  - `CommandMonitor` (every 2 s, `command-monitor` lease): confirms a sent command from a report received
    after the send (an older matching one for `already-in-effect`); times out after 60 s (twice the report
    interval for a polled device) and reconciles: confirm, resend with the same key (new outbox entry in the
    same write) or dead-letter; announces a due, free, unclaimed command 5 s after it became sendable, then
    every dispatch timeout (2 min), counting only those, and dead-letters after three ("dispatch kept
    failing"); expires claims. A command waiting for a busy device is never announced. A route that throws
    releases its claim, so the message reaches the dead-letter queue.
  - API: `POST /v1/plans/{id}/confirm` (202 with the plan and its commands; 404, 409, 410),
    `POST /v1/devices/{id}/commands` (202 with `Location`; body checked field by field; 404; 422
    `capability-not-supported` or `unprocessable` for a removed device or a power above the maximum; the
    command id is its idempotency key) and `GET /v1/commands/{id}` (history with `reason`). Both POSTs are
    `.Idempotent()`; the request's `traceparent` is stored on the command and its outbox entry. No endpoint
    answers or documents 501.
  - `worker.dispatch` (`CommandRequested`) and `worker.command-results` (`GatewayCommandResult`) with delay and
    dead-letter queues; `SendToQueueAsync` (default exchange, `mandatory`: an unroutable publish fails) and
    `PublishSerializedAsync` for saved messages.
  - `CommandsOptions` (section `Commands`, validated naming the key) and the `outbox-relay` and `command-monitor`
    leases. `OutboxRelay`: due entries oldest first, marked published only after the broker confirms, the
    command moved to `Published`, backing off from 1 s to 30 s while the broker is down.
  - `CommandDispatcher`: acks without a send when the command is not ready (terminal, sent, not due, waiting
    for a retry, its device busy or holding an earlier-due command), else claims, routes and applies the
    outcome through Core (`CommandOutcomes`: accepted, rejected, retry no sooner than `Retry-After`, dead-letter
    after the sixth attempt). Routes: `ProviderCommandRoute` (the owner's account, `ICommandSender`) and
    `GatewayCommandRoute` (connection registry, the replica's queue; no connection or a gone queue is
    transient). `CommandResultConsumer` applies gateway results and drops duplicates and late ones.
- **Messaging (RabbitMQ, ADR-003):**
  - The topology is declared from code: topic exchange `cheaphours.events`; per consumer a quorum queue,
    three delay queues (5 s, 30 s, 2 min) and a dead-letter queue. A conflicting queue stops startup.
    Every name carries `Messaging:QueuePrefix`.
  - The confirmed, persistent, `mandatory` publisher has a 5 s timeout, and the envelope travels in the
    AMQP properties.
  - `BatchingConsumer<T>`: batches of 200 or 250 ms. A failing batch is split. Retries go through the
    delay queues, then to the dead-letter queue with attempts and a category. Unreadable bodies and newer
    schema versions are parked at once. A database outage keeps the batch and backs off without
    spending retries. Stale delivery tags are never acknowledged after a reconnect. `x-delivery-limit` 20
    parks a message that crashes its consumer.
  - The connection recovers by itself, and only `host:port` and exception type names are logged.
- **Telemetry storage (worker role, ADR-002):**
  - `telemetry` is a time-series collection (`ts`, `meta`, 30-day TTL) and `inbox` has a 7-day TTL; both
    are created at startup.
  - `TelemetryBatchWriter` stores readings exactly once without transactions:
    - the inbox drops handled ids;
    - a repeat first deletes its own partial write;
    - the inbox entry is written last.
  - `devices.lastReading` only moves forward, and power is derived from the energy change when it is
    not reported (`powerDerived`).
  - The worker role runs startup (indexes and topology) and then the telemetry consumer.
- **Device read API (api role, D3):**
  - `GET /v1/devices` (oldest first, cursor paging), `GET /v1/devices/{id}` and
    `GET /v1/devices/{id}/telemetry` (15-minute buckets aggregated inside MongoDB: energy increments
    between consecutive readings, counted only across gaps of at most 15 minutes and never across a meter
    reset; average power = energy over the slot; last reported SoC; empty slots left out).
  - Owner scoping is part of every query; another owner's device gets the same 404 as a missing one.
  - Presence on read: the gateway stores each charger's heartbeat interval as its report interval, and
    a device silent for more than three intervals is shown `Offline` even when no replica marked it.
  - Query validation before any lookup (limit 1–200, UTC range of at most 31 days, `resolution` only
    `15m`, opaque base64url cursors), with `errors` keyed by parameter.
  - API keys live in `apiKeys` (by SHA-256 hash); configured keys are validated at startup and seeded in
    the background; a malformed stored key gets 401 and is logged by name, never by hash.
  - The API role retries MongoDB setup in the background and serves `/health/live` and `/health/ready`
    (not in the OpenAPI document).
  - Contract corrections: `ReadingDto.powerW` is nullable; timestamps are typed `string`/`date-time` and
    nullable ones (`lastSeenAt`, `expiresAt`) allow null.
- **Persistence:** `chargePoints`, `devices` (with `removedAt`), `connections`, `telemetry`, `inbox`,
  `apiKeys`, `providerAccounts` (encrypted tokens with a version, reconnect mark, poll schedule; one
  account per owner and provider), `locks`, `prices`, `priceDays`, `goals`, `goalClaims`, `plans` and
  `idempotencyKeys` (24-hour TTL); the owner-scoped device reader and the telemetry bucket reader; index
  creation; camelCase and enum-name conventions; `schemaVersion` on every document.
  - `commands`: rebuilt through `Command.Restore` (a stored state that disagrees with the history throws);
    every write filters on the version it read and increments it; a unique idempotency key; claims that
    exactly one dispatcher wins; scans for the monitor, the per-device queue and a plan version.
  - `outbox`: entries added only inside the transaction of the change they announce; the relay's due
    listing (partial index on `published: false`); published entries expire after 7 days.
  - Goal transactions with commands: confirming a draft (the running version superseded and its unsent
    commands cancelled with the reason, a change draft's values applied to the goal and its claims, the new
    commands stored with their outbox entries), a change draft above a running version, and re-planning a
    confirmed plan. A plan that moved on, or a write that fails part-way, leaves nothing.
  - `devices.statusReceivedAt` (receipt time of the latest reported status) and `activeSessionId`;
    `proposedTargetSocPercent`/`proposedDepartureAt` on plan versions; the running (highest confirmed)
    version; an owner's account at a provider.
- **Greedy planner (`CheapHours.Planning`, pure, ADR-005):** the energy needed from the charge gap,
  battery and efficiency (0.90); the window of whole UTC slots from now to departure minus the safety
  margin (30 min), counted in UTC so daylight-saving nights hold 36 or 28 slots; every slot at the
  charger's maximum power; the cheapest priced slots first (ties to the earlier slot, negative prices
  welcome), unpriced slots only when needed, earliest first, flagged `pricesComplete: false`; every slot
  and `targetMissed` when the window is too short; expected and arrival costs in unrounded EUR.
  `PlannerOptions` with range checks naming the key; `PlanProgress.ExpectedSoc` for the drift check.
  FsCheck checks the four planner properties over 1,000 cases each, with windows across both 2026
  daylight-saving changes, negative and missing prices.
  - `PlanCommandSchedule`: each run of consecutive slots becomes a start at its first slot and a stop at
    the end of its last, keyed by `PlannedCommandKey`. A re-plan of a version charging right now continues
    without a gap: no start for a run that carries on, otherwise a stop at the end of the current slot.
    FsCheck (1,000 cases each): every start has a later stop, keys never collide, nothing is due after the
    window.
- **Day-ahead price import (worker):**
  - `EnergyChartsClient` behind `IPriceSource` reads one Europe/Berlin day per request (92, 96 or 100
    slots; `BerlinTime` falls back to the Windows zone id under invariant globalization) through its own
    pipeline: 10 s per attempt, 30 s in total, jittered retries for 5xx, 408, timeouts and connection
    failures, never for 429. A closed result: published, not published (404), rate-limited (429),
    rejected (a broken rule) or failed. Bodies are never logged.
  - `PriceResponseValidator` checks the unit, equal array lengths, the day's first slot, 15-minute steps
    inside the day and prices from −500 to 4000; a partial day or null prices keep the priced slots.
  - `prices` (one document per zone and slot, Decimal128; a re-import modifies nothing) and `priceDays`
    (complete, announced and given-up marks, each set once).
  - `PriceImportJob` runs at start and then hourly, never later than 13:00 Berlin, under the
    `price-import` lease: today until complete, tomorrow from 13:00 to 20:00 with a 2 s pause, at most two
    requests a run; a 429 ends the run; one warning when tomorrow is given up (a worker started after 20:00
    tries tomorrow once first, so a late start still picks up published prices). A complete day is announced
    once with `PricesPublished` (id derived from zone and day; announced by the next run when the broker
    was down). `Prices:ImportEnabled` turns it off; integration tests never reach the real API (recorded
    fixtures for 2026-03-29, 2026-10-25 and 2026-11-02, CC BY 4.0, and WireMock).
  - Named leases (`NamedLease`, keyed `ILease` per name: `device-polling`, `price-import`, `plan-drift`)
    and `LeasedRun` (take, run with a deadline of half the lease, release in `finally`).
- **Goals and plan versions:** `goals`, `goalClaims` (one open goal per vehicle and charger until its
  departure) and `plans` (`{planId}:v{n}`, so concurrent writers store one version). Creating a goal
  (claims, goal, version 1) and changing one (claims, goal, new version, previous `Superseded`) are single
  MongoDB transactions. `GoalPlanningService` is the one planning path for the API and the worker: 404 for
  another owner's device, 422 checks (past, no whole slot, more than 48 h, wrong type, same device, no
  charge, target below charge), 409 for a busy device; a re-plan that changes nothing stores nothing;
  drafts expire after 10 minutes.
  - `ConfirmAsync`: 404, then 410 for an expired draft, then 409 when the current version is not a draft;
    the runs become commands for the goal's charger (`PlanCommandSchedule`, keys per version and slot) and
    are written with the confirmation, superseding the running version and cancelling its unsent commands.
  - Changing a goal whose plan is confirmed stores a change draft (`proposedTargetSocPercent`,
    `proposedDepartureAt`) and leaves the goal and the running version alone; confirming it applies the
    values to the goal and its claims. An expired change draft changes nothing.
  - A re-plan on prices or drift uses the running (highest confirmed) version as its base, from the goal's
    stored values, and writes a new confirmed version that reschedules the commands: no start for a run
    that carries on the charging under way (judged from all the plan's commands), otherwise a stop at the end
    of the current slot; a change draft above it is superseded.
- **Re-planning (worker):** the `worker.planner` queue (bound to `PricesPublished`, with delay and
  dead-letter queues) re-plans every open goal whose window reaches the published day; a repeated message
  adds nothing and a database outage pauses the consumer. `DriftCheckService` (every 5 minutes, `plan-drift`
  lease) re-plans the running confirmed version from the vehicle's reported charge when it is 5 points or
  more off the charge the plan expects at that time.
- **Planning API (api role):**
  - `GET /v1/prices` (zone must be `DE-LU`, the range widened to the 15-minute grid, at most 31 days,
    cursor paging) and `GET /v1/savings` (goals departing in the range whose latest confirmed version had
    complete prices; zeros until plans can be confirmed).
  - `POST /v1/goals` (`201` with `Location: /v1/goals/{id}/plan`, the goal and its draft), `PATCH
    /v1/goals/{id}` (a new draft version; `409` for a confirmed plan or a past departure) and `GET
    /v1/goals/{id}/plan` (a draft past its expiry shows `Expired`). Costs are shown to the cent, half away
    from zero, and savings are the difference of the rounded costs. Another owner's goal or device gets
    the same `404` as a missing one.
  - `IdempotencyMiddleware` on endpoints marked `.Idempotent()` (`POST /v1/goals`): a key of 1–255
    visible ASCII characters per owner for 24 hours; the same request replays the first response (4xx
    included), another body gets `409 idempotency-key-reused`, a repeat while the first runs gets `409
    conflict` (taken over after a minute), and responses of 500 and above are not kept.
  - `GET /v1/goals`: the owner's goals newest first (by `_id`, so by creation), cursor `g1:` and `limit` 1–200,
    each with its current plan's id, version, status and expiry through the same mapping as `PlanDto` (an expired
    draft shows `Expired`; a change draft above a confirmed version is the current plan). Two queries per page:
    `IGoalStore.ListAsync` and `IPlanStore.GetCurrentManyAsync` (one aggregation); a goal without a plan is a
    500.
  - The README walks through today's prices and a demo goal for the simulated car and wallbox.
- **Owner dashboard (`dashboard/`):** a static page on the public API only, no build step and no packages.
  - Sections: devices (all pages), today's and tomorrow's `DE-LU` prices per Berlin day (23/24/25 h; "not
    published yet"), goals with "more" and a form to set one (field errors from a 422 next to their fields), a
    goal's plan on the period's prices with the three amounts, notices and a draft's expiry, confirming a draft
    and following its commands until final, and 30-day savings. Each section shows its own problem or "API
    unreachable".
  - Readable for the owner: devices by name ("Charger", "Vehicle 2") with their facts ("11.0 kW max · via OCPP"),
    ids only in hover titles; prices in ct/kWh; times as "Today, 07:00" / "Tomorrow, 07:00"; a plan as charging
    runs with their duration, its chart from now to departure so the skipped dearer slots show; commands by what
    they do; no power shown for an offline device.
  - The key lives in session storage only and travels only in `X-Api-Key`; a 401 drops it and asks again;
    "forget key" removes it. A read key does everything but confirm, which says an execute key is needed.
  - Modules: `api.mjs` (the only `fetch`), `time.mjs` (Berlin display and Berlin→UTC without a library;
    skipped spring hour moves forward, repeated autumn hour takes the first instant), `format.mjs`,
    `paging.mjs`, `poll.mjs` (2 s for a minute, then 30 s, only while visible), `submit.mjs` (one
    Idempotency-Key per submission, reused only after no response), `chart.mjs`, `plan.mjs`; `app.mjs` alone
    touches the DOM and never parses HTML.
  - `DashboardContractTests` checks every `request('<METHOD>', '<path>'` against the committed OpenAPI
    document and refuses calls written another way; Node tests cover the logic.
- **Observability (ADR-009):** every role registers OpenTelemetry through
  `ObservabilitySetup` (service name `cheaphours-{role}`, instance id = gateway replica id or machine name; the
  CheapHours sources and meters, ASP.NET Core without `/health` and `/ocpp`, HttpClient, `System.Runtime`), exports
  traces, metrics and logs over OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, and writes JSON console logs
  outside Development (UTC, scopes, trace and span ids). Architecture tests keep the OpenTelemetry SDK in the Host
  and spans and metrics out of Core and Planning.
  - Traces: messages keep the trace context they were created with; publish and handling spans
    (`MessagingTracing`, `SpanScope`) are its children and never rewrite the `traceparent`. A command is one trace
    from the API request through the outbox publish, `dispatch {CommandType}` (with the vendor HTTP call), the
    gateway's handling and `ocpp {Action}` call, the result and the monitor's `command confirm` / `reconcile` /
    `resend` / `reannounce` / `dead-letter` (an error span). Each OCPP frame, webhook request, `poll {providerId}`
    and `price-import` run starts a trace that its `TelemetryReceived` or `PricesPublished` carries; re-planning
    continues the import's trace; a `telemetry store` batch links to its messages' traces. Failed spans carry the
    exception type only.
  - Log scopes (`LogScopes`): `commandId`, `deviceId`, `providerId` around work on a command; `providerId` and
    `externalId` around frames, webhooks and poll results.
  - Metrics, tagged only `provider.id`, `outcome`, `command.type`, `reason`, recorded after the stored change:
    vendor calls (`cheaphours.provider.requests` / `.request.duration`, OCPP command calls as `ocpp`: `success` or
    `transient`, none for already-in-effect); `cheaphours.provider.circuit.state` per HTTP provider from the breaker
    callbacks (`ProviderCircuitStates`, 0 from startup); `cheaphours.command.confirmation.duration` (due → confirmed);
    `cheaphours.command.dead_lettered` (`attempts-exhausted`, `dispatch-kept-failing`, `not-confirmed`);
    `cheaphours.outbox.lag` from the relay's due listing on a pass run under the lease (cleared when a pass finds
    the lease taken); `cheaphours.gateway.connections` per replica; `cheaphours.telemetry.readings` from what
    `TelemetryBatchWriter.WriteAsync` returns it stored per provider.
  - Tests observe spans and metrics in-process (`tests/Shared/Observability`: `SpanRecorder`, `MetricRecorder`);
    the export tests post to a WireMock OTLP receiver.
- **CI pipeline and image (ADR-010):**
  - `.github/workflows/ci.yml` on pull requests to `main`, pushes to `main`, `v*` tags and by hand; parallel jobs, each
    with a time limit: `lint` (actionlint and hadolint from images pinned by digest, the compose file with
    `.env.example` and throwaway secrets), `test` (locked restore, Release build, Unit, Architecture, Api and Contract
    tests), `integration` (Testcontainers on the runner's Docker, fault tests included), `dashboard` (Node 22 and 24),
    `image` (amd64 build with the `gha` cache, `scripts/ci/image-smoke.sh`, Trivy failing on a fixable HIGH or
    CRITICAL, SARIF to code scanning except for forks). xUnit TRX results kept 14 days, also when tests fail.
  - `publish` needs every job and runs only for pushes to `main` or `v*`: amd64 and arm64 from the same cache, pushed
    to `ghcr.io/shakerkamal/cheaphours` with `sha-<short>`, `main` or `X.Y.Z`/`X.Y` (no `latest`), OCI labels, SBOM,
    BuildKit provenance and `actions/attest-build-provenance`. It alone gets `packages`, `id-token` and
    `attestations: write`; the workflow default is `contents: read`; no repository secret; pull-request runs are
    cancelled by a newer push, `main` and tag runs queue.
  - `codeql.yml` (C# built as CI builds it, JavaScript; `security-extended`; pull requests, `main`, weekly) and
    `dependabot.yml` (weekly, grouped: NuGet, Actions, each Dockerfile's base image, compose images).
  - `WorkflowPolicyTests` (ArchitectureTests) reads the workflows as text: actions pinned to a 40-hex SHA with a
    `# v…` comment, workflow permissions exactly `contents: read`, publish-only write scopes, `timeout-minutes` on every
    job, no `pull_request_target`; each rule proven by a planted workflow.
  - `src/CheapHours.Host/Dockerfile`: the build stage on `$BUILDPLATFORM` (SDK pinned by digest, locked restore layer,
    framework-dependent publish once for both platforms); runtime `aspnet:10.0.14-noble-chiseled-extra` by digest (no
    shell, tzdata for Berlin, user 1654; the newest 10.0 patch because 10.0.12's runtime fails the scan), entrypoint
    `dotnet CheapHours.Host.dll`, the role as arguments, port 8080. `appsettings.Development.json` is not published.
    Every Dockerfile's `USER` is numeric (hadolint DL3066).
  - Every project commits `packages.lock.json`; `NuGetAudit` (all, low) makes a vulnerable package fail the restore;
    `ContinuousIntegrationBuild` on GitHub Actions.
  - The gateway seeds its charger registrations in one ordered bulk write, so startup does not grow with the number
    of chargers (found under full-suite load, where one write took 300–700 ms).
  - Found by the first pull-request run: every MongoDB test container comes from `MongoFixture.NewContainerBuilder()`,
    which sets `GLIBC_TUNABLES=glibc.pthread.rseq=1`, because MongoDB 8 refuses to start on the runner's kernel 7.0
    (SERVER-121912); and the heat-pump simulator creates its delivery timer in `StartAsync`, because .NET 10 runs
    `BackgroundService.ExecuteAsync` on the thread pool and a test could advance the fake clock before the timer existed.
- **ADRs:** 001 (modular monolith), 002 (time-series telemetry), 003 (RabbitMQ), 004 (own outbox and
  inbox), 005 (greedy cheapest-slot planner), 006 (OCPP 1.6J smart-charging messages), 008 (gateway command
  routing), 009 (OpenTelemetry; messages keep their trace context), 010 (images built only by CI, scanned, attested).
- **Reliability catalogue:** `docs/reliability.md` maps every row of the System Design reliability table to
  its tests by class and method; `ReliabilityCatalogueTests` fails when a named test no longer exists. Gate
  fault tests (`tests/CheapHours.IntegrationTests/Faults`): a worker stopped between save and publish, the
  broker stopped while three commands are accepted, a charger dropping mid-command and reconnecting
  `Available`; each command reaches its device exactly once.
- **Tests:** 1780 green and 31 skipped by design (contract rules that do not fit a provider), plus 51 Node
  tests for the dashboard (`node --test "dashboard/test/*.test.mjs"`): unit,
  property, architecture, API contract, provider contract, and integration tests against a
  real MongoDB and RabbitMQ in Testcontainers, with a real gateway on Kestrel, a real worker host and
  the API role on its own database (one model-based FsCheck test checks the bucket pipeline; four planner
  property tests run 1,000 cases each; a golden test plans against recorded real prices).
  The integration tests include broker and database fault tests, and the observability tests
  (`tests/CheapHours.IntegrationTests/Observability`: spans, log scopes and metrics checked in-process, OTLP export to
  a WireMock receiver). `dotnet test` needs Docker running.

### Left to build

- **Delivery:** Helm chart (deploys the published `sha-` image), k6 load test, README with architecture diagram and
  demo.
- **CheapHours Assistant (separate repo):** MCP server and agent host over the public API only.

### Later, if the project continues (not planned; decided with the user)

- **Vendor account linking:** replace seeding refresh tokens from configuration with a real one-time
  consent. The car-cloud simulator gets the OAuth 2.0 authorization-code flow with PKCE (an authorize
  page, `state` checks, code exchange at `/oauth/token`). CheapHours gets a "link account" endpoint that
  redirects to it and a callback that exchanges the code and stores the refresh token through the
  existing `IProviderAccountRepository`, which also re-links accounts marked reconnect-needed. The token
  store, rotation, encryption and revocation handling stay as they are. This widens D3 (one seeded demo
  owner) only for linking; our own users stay out (no identity provider, per the Project Idea page). Its
  own change when picked up.

### Open items

- Waiting for the user's review: the database ERD.
- D8: rename "WattBridge" to CheapHours in Notion (user).
- D9: needs the week-by-week roadmap from the user.

### Decisions

| # | Topic | Status |
|---|---|---|
| D1 | Worker → charger via per-replica gateway queue | Accepted: [ADR-008](docs/adr/008-gateway-command-routing.md); routing key in `GatewayCommand`; connection registry built |
| D2 | Goal has vehicle + charger; typed device attributes | Accepted: `Goal`, `DeviceAttributes` |
| D3 | `apiKeys` + `ownerId`; one seeded demo user | Accepted: `apiKeys` in MongoDB seeded from configuration; every device read is owner-scoped |
| D4 | 15-minute slots everywhere | Accepted: `PlanSlot` |
| D5 | Per-slot Start/Stop/SetPowerLimit in core | Accepted: `CommandType`; per-slot plans recorded in [ADR-005](docs/adr/005-greedy-cheapest-slot-planner.md) |
| D6 | `IWebhookVerifier` per provider | Accepted: interface built; first implementation `HeatPumpCloudProvider` (HMAC-SHA256 over timestamp and raw body) |
| D7 | Command states `Pending, Published, Sent, Confirmed, Reconciling, Rejected, DeadLettered, Cancelled` | Accepted with 8 states (`Cancelled` added with the user); the Notion diagram still shows 7 (user to update) |
| D8 | Rename "WattBridge" to CheapHours in Notion | Open (Notion edit, user) |
| D9 | Map the plan onto the week-by-week roadmap | Needs the roadmap from the user |

### Package versions

Pinned in `Directory.Packages.props`. `Microsoft.*` 10.0.x packages match the installed runtime patch
(10.0.12). Every project commits a `packages.lock.json`: after changing a version, run `dotnet restore` once and
commit the lock files (CI restores with `--locked-mode`).

| Package | Version | Used by |
|---|---|---|
| Microsoft.AspNetCore.DataProtection.Abstractions | 10.0.12 | Providers.Kit (token encryption; the host has the full stack in its shared framework) |
| Microsoft.AspNetCore.OpenApi | 10.0.12 | Host (API role) |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.12 | Providers.Kit |
| Microsoft.Extensions.Http.Resilience | 10.10.0 | Providers.Kit (per-provider pipeline; brings Polly 8.4.2 and System.Threading.RateLimiting; 10.11.0 needs 10.0.13 packages) |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.12 | Providers.Kit, Persistence |
| MongoDB.Driver | 3.12.0 | Persistence (and Host through it) |
| NJsonSchema | 11.6.1 | Providers.Ocpp (OCPP 1.6 schemas are draft-04; brings in Newtonsoft.Json) |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.20.1 | Host only (OTLP export of traces, metrics and logs) |
| OpenTelemetry.Extensions.Hosting | 1.20.1 | Host only (tracer, meter and logger providers) |
| OpenTelemetry.Instrumentation.AspNetCore | 1.20.1 | Host only (request spans) |
| OpenTelemetry.Instrumentation.Http | 1.20.1 | Host only (vendor HTTP call spans) |
| RabbitMQ.Client | 7.2.2 | Messaging (async-only API; publisher confirms with tracking) |
| Scalar.AspNetCore | 2.18.0 | Host (Development only) |
| FsCheck.Xunit.v3 | 3.5.0 | UnitTests (property tests), IntegrationTests (model-based telemetry bucket test) |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | ApiTests |
| Microsoft.Extensions.TimeProvider.Testing | 10.11.0 | UnitTests |
| Testcontainers.MongoDb | 4.16.0 | IntegrationTests |
| Testcontainers.RabbitMq | 4.16.0 | IntegrationTests |
| TngTech.ArchUnitNET.xUnitV3 | 0.13.4 | ArchitectureTests |
| WireMock.Net.Minimal | 2.19.0 | Providers.Kit.Testing (vendor fakes for the contract suite) |
| xunit.v3 | 4.0.2 | all tests |
| xunit.v3.assert, xunit.v3.extensibility.core | 4.0.2 | Providers.Kit.Testing (abstract contract tests; a library cannot reference the runner package) |

The dashboard uses no npm packages; its tests need Node 22+ only. Container images are pinned in
`docker-compose.yml` (the dashboard's web server: `nginx:1.30.5-alpine`, nginx's stable line).

The image and the pipeline pin by digest: `mcr.microsoft.com/dotnet/sdk:10.0.401` (build stage) and
`mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra` (runtime; the newest 10.0 patch, ahead of the
packages, because 10.0.12's runtime has fixable HIGH vulnerabilities) in `src/CheapHours.Host/Dockerfile`;
`rhysd/actionlint:1.7.12`, `hadolint/hadolint:v2.15.1` in `ci.yml`, and Trivy 0.76.2 (`aquasec/trivy` locally,
`version:` of the Trivy action in CI). Actions are pinned by commit SHA with the version in a comment: `actions/checkout`
v7.0.1, `actions/setup-dotnet` v6.1.0, `actions/setup-node` v7.1.0, `actions/upload-artifact` v7.0.2,
`docker/setup-buildx-action` v4.4.1, `docker/login-action` v4.8.0, `docker/metadata-action` v6.2.0,
`docker/build-push-action` v7.4.0, `aquasecurity/trivy-action` v0.36.0, `github/codeql-action` v4.39.3,
`actions/attest-build-provenance` v4.2.2.

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
dotnet test                                    # all tests; integration tests need Docker running
dotnet test --project tests/CheapHours.UnitTests --filter-class "*RoleParserTests"   # one class (MTP/xUnit v3 syntax)
dotnet test --filter-method "*Role*"           # by method name; VSTest-style --filter is not supported
dotnet run --project src/CheapHours.Host -- --role api      # or gateway | worker (Development via launchSettings)
docker compose up -d --wait                    # local MongoDB, RabbitMQ, the three vendor simulators, the Aspire dashboard and the owner dashboard on localhost:5070 (needs .env, see README)
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 dotnet run --project src/CheapHours.Host -- --role worker   # export to the dashboard (any role); open http://localhost:18888
dotnet test --project tests/CheapHours.IntegrationTests --filter-class "*CommandTraceTests"   # one trace from the API call to the charger's confirmation; bounded metric tags
dotnet run --project src/CheapHours.Host -- --role gateway --urls http://localhost:5081   # the charger connects and heat-pump webhooks arrive here
dotnet run --project src/CheapHours.Host -- --role worker      # stores telemetry; polls the car cloud when Providers:CarCloud is set; imports prices (--Prices:ImportEnabled=false to stop) and re-plans goals
dotnet test --project tests/CheapHours.UnitTests --filter-class "*GreedyPlannerPropertyTests"   # the planner's four properties, 1,000 cases each
dotnet test --project tests/CheapHours.UnitTests --filter-class "*GoldenPlanTests"              # the plan for the recorded 2026-11-02 prices
dotnet test --project tests/CheapHours.ContractTests             # the shared provider contract suite (no Docker needed)
node --test "dashboard/test/*.test.mjs"        # the dashboard's logic (Node 22+, no packages); part of the gate
dotnet restore --locked-mode                   # what CI restores; fails when a lock file is stale or a package is vulnerable
docker build -f src/CheapHours.Host/Dockerfile -t cheaphours:local .   # the one image (role as argument: --role api)
scripts/ci/image-smoke.sh cheaphours:local     # the image checks CI runs: liveness, unknown role, non-root, Berlin tzdata, no secrets
MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:1.7.12 -color   # lint the workflows (Git Bash; elsewhere "$PWD")
for f in $(git ls-files '*Dockerfile'); do docker run --rm -i hadolint/hadolint:v2.15.1 < "$f"; done   # lint every Dockerfile
dotnet test --project tests/CheapHours.ArchitectureTests --filter-class "*WorkflowPolicyTests"   # pinned actions, least privilege, time limits
dotnet test --project tests/CheapHours.ApiTests --filter-class "*DashboardContractTests"   # every dashboard call is in docs/api/openapi.v1.json
dotnet test --project tests/CheapHours.IntegrationTests --filter-class "*CommandFaultTests"   # the command-dispatch gate: worker, broker and charger killed mid-command
curl -s -X POST -H "X-Api-Key: <execute key>" -H "Idempotency-Key: $(uuidgen)" http://localhost:5080/v1/plans/<planId>/confirm   # confirm a draft; follow each command at /v1/commands/{id}
dotnet run --project simulators/CheapHours.Simulators.CarCloud -- --Simulator:ClientSecret=<s> --Simulator:Accounts:0:RefreshToken=<t> --Admin:Enabled=true   # simulator without Docker
dotnet run --project simulators/CheapHours.Simulators.HeatPumpCloud -- --Simulator:CallbackUrl=http://localhost:5081/webhooks/heatpumpcloud/acct_demo --Simulator:SigningSecret=<s> --Admin:Enabled=true   # heat-pump simulator without Docker
curl -s -X POST http://localhost:5091/admin/heat-pumps/hp_1/redeliver   # resend the last event unchanged (adds no reading)
dotnet run --project src/CheapHours.Host -- --role api --urls http://localhost:5080   # device API (demo key: README "Run locally")
UPDATE_SNAPSHOT=1 dotnet test --project tests/CheapHours.ApiTests   # deliberately refresh docs/api/openapi.v1.json
openspec validate --all --strict               # check all changes and specs
openspec status --change <name>                # artifact progress
```

## Repo layout

```
src/CheapHours.Core                  domain types; references nothing, no I/O
src/CheapHours.Messaging.Contracts   message records + shared JSON options; references Core only
src/CheapHours.Planning              greedy planner, planner options, plan progress (pure, Core only)
src/CheapHours.Persistence           MongoDB stores: chargePoints, devices, connections, telemetry, inbox, apiKeys, providerAccounts, locks, prices, priceDays, goals, goalClaims, plans, idempotencyKeys, commands, outbox; readers; index creation
src/CheapHours.Messaging             RabbitMQ connection, topology, confirmed publisher, batching consumer
src/CheapHours.Providers.Kit         adapter interfaces, registration, resilience
src/CheapHours.Providers.Kit.Testing shared provider contract suite, WireMock vendor fake, in-memory account store
src/CheapHours.Providers.{Ocpp,CarCloud,HeatPumpCloud}
src/CheapHours.Host                  single entry point: --role api | gateway | worker
simulators/                          vendor stand-ins that reference no src project (CheapHours.Simulators.CarCloud, .HeatPumpCloud)
tests/                               UnitTests, ArchitectureTests, ApiTests, ContractTests, IntegrationTests (+ later EndToEnd)
docker/ocpp-simulator/               pinned build of the OCPP 1.6 charge-point simulator
dashboard/site, dashboard/test       owner dashboard (static HTML, CSS, ES modules) and its Node tests
docker/web/nginx.conf                web server for the dashboard, forwarding /v1 to the API
docker-compose.yml, .env.example     local stack; .env is git-ignored
docs/api/openapi.v1.json             committed public contract
docs/adr/                            decision records
docs/reliability.md                  reliability table → tests (checked by ReliabilityCatalogueTests)
src/CheapHours.Host/Dockerfile       the one image for every role (built and published by CI only, ADR-010)
.github/workflows/                   ci.yml (lint, tests, image, publish) and codeql.yml; .github/dependabot.yml
scripts/ci/                          image-smoke.sh, run by CI and locally
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
- Integration (`tests/CheapHours.IntegrationTests`): one Testcontainers MongoDB replica set per run
  (assembly fixture), a fresh database per test class or gateway host; the gateway runs on real Kestrel
  (`WebApplicationFactory.UseKestrel`) with real `ClientWebSocket` chargers, and a RabbitMQ container per
  run. Worker tests run real worker hosts (two for the polling leader) and the car-cloud simulator
  in-process on Kestrel with its own `FakeTimeProvider`. Webhook tests post signed webhooks to the
  gateway on Kestrel; the heat-pump simulator runs in-process with a fake clock and a recording handler,
  and once over real HTTP into the gateway end to end. `GatewayFactory` passes its overrides as host
  settings too, because the gateway reads `Providers:HeatPumpCloud` while it registers services.
- Contract (`tests/CheapHours.ContractTests`): each provider subclasses `ProviderContractTests<T>` with a
  harness; HTTP providers run against `WireMockVendorFake`; webhook verifiers get signed samples from
  their harness; inapplicable rules report as skipped with a reason. The suite is proven against
  deliberately broken providers, the CarCloud facts also run against the real simulator, and webhooks
  captured from the real heat-pump simulator must verify and translate with the provider.
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
- Commit anything under `openspec/`, `docs/plan.md`, or the OpenSpec tooling (`.claude/commands/opsx/`,
  `.claude/skills/openspec-*`, `.agents/`).
