# CheapHours

A .NET 10 backend that connects energy devices from different vendors behind one device model and
charges an electric car in the cheapest 15-minute slots before the driver leaves. Commands to devices
are never lost and never applied twice.

> **Status:** in progress. What is built and what is left is tracked in [AGENTS.md](AGENTS.md).

## What it does

- Connects three kinds of provider through one adapter kit: an OCPP 1.6J wallbox (device pushes),
  a car-maker cloud polled over OAuth 2.0 (we pull), and a heat-pump cloud that calls webhooks (vendor pushes).
- Imports German day-ahead prices (zone DE-LU) and plans the cheapest charge before departure.
- Sends start, stop and power-limit commands, and counts one as done only when the device confirms it.
- Offers a versioned public REST API (`/v1`) that the dashboard and an AI assistant both use.

## Prerequisites

- .NET SDK 10.0.401 (pinned in `global.json`)
- Docker (Docker Desktop on Windows or macOS) for the local stack and the integration tests; `dotnet test`
  needs a running Docker engine
- Node.js 22 or later, only to run the dashboard's tests (no packages to install)

## Build and test

```sh
dotnet build
dotnet test
node --test "dashboard/test/*.test.mjs"
```

The build treats every warning as an error. Tests run on Microsoft.Testing.Platform (xUnit v3). The dashboard's
logic is tested with Node's built-in test runner.

## Run

One executable, three roles:

```sh
dotnet run --project src/CheapHours.Host -- --role api
dotnet run --project src/CheapHours.Host -- --role gateway
dotnet run --project src/CheapHours.Host -- --role worker
```

## Run locally

The local stack is MongoDB (a single-node replica set), RabbitMQ, an OCPP 1.6 charge-point simulator, a
car-cloud simulator, a heat-pump-cloud simulator, the owner dashboard's web server and the telemetry
dashboard. The charge point connects to the gateway running on your machine, the worker polls the car cloud,
the heat-pump cloud posts signed webhooks to the gateway, and the web server forwards the dashboard's `/v1`
calls to the API.

1. **Make a charger password.** It is 20 random bytes written as 40 hex characters. Print it and its
   SHA-256:

   ```powershell
   $p = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(20)).ToLowerInvariant(); $p
   [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($p))).ToLowerInvariant()
   ```

   ```sh
   p=$(openssl rand -hex 20); echo "$p"; printf %s "$p" | sha256sum | cut -d' ' -f1
   ```

2. **Give the password to the simulator and the hash to the gateway.** Copy `.env.example` to `.env`
   (git-ignored) and set `CP_PASSWORD`. Then store the hash in the gateway's user secrets:

   ```sh
   dotnet user-secrets --project src/CheapHours.Host set "Providers:Ocpp:ChargePoints:0:PasswordHash" <hash>
   ```

   The gateway only ever sees the hash. The simulator logs its own settings when it connects,
   password included, so don't share its container logs.

3. **Give RabbitMQ an account.** Set `RABBITMQ_PASSWORD` in `.env` (generate it like the charger
   password) and give the gateway and worker the matching connection string:

   ```sh
   dotnet user-secrets --project src/CheapHours.Host set "ConnectionStrings:RabbitMq" "amqp://cheaphours:<password>@localhost:5672/"
   ```

   RabbitMQ only reads the account on its first start, so after changing it run `docker compose up -d
   --force-recreate rabbitmq`. The topology (exchange, queues, retries, dead letters) is declared by
   the gateway and worker when they start.

4. **Make a demo API key.** A key is 32 random bytes as 64 hex characters; the API stores only its
   SHA-256. Print both, keep the key for the requests below, and give the API the hash for the demo
   owner `own_demo` (the owner the simulator's charger is registered to):

   ```powershell
   $k = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant(); $k
   [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($k))).ToLowerInvariant()
   ```

   ```sh
   k=$(openssl rand -hex 32); echo "$k"; printf %s "$k" | sha256sum | cut -d' ' -f1
   ```

   ```sh
   dotnet user-secrets --project src/CheapHours.Host set "ApiKeys:Keys:0:Name" demo
   dotnet user-secrets --project src/CheapHours.Host set "ApiKeys:Keys:0:Hash" <hash>
   dotnet user-secrets --project src/CheapHours.Host set "ApiKeys:Keys:0:Owner" own_demo
   dotnet user-secrets --project src/CheapHours.Host set "ApiKeys:Keys:0:Scopes:0" read
   dotnet user-secrets --project src/CheapHours.Host set "ApiKeys:Keys:0:Scopes:1" execute
   ```

   The API checks the configured keys when it starts and copies them into MongoDB (`apiKeys`); a key
   can also be added there directly, without a restart.

5. **Link the simulated car cloud.** Set `CARCLOUD_CLIENT_SECRET` and `CARCLOUD_SEED_REFRESH_TOKEN` in
   `.env` (generate each like the charger password) and give the worker the same values, plus where the
   simulator listens and whose account it is (`acct_demo`, owned by `own_demo`):

   ```sh
   dotnet user-secrets --project src/CheapHours.Host set "Providers:CarCloud:BaseAddress" http://localhost:5090/
   dotnet user-secrets --project src/CheapHours.Host set "Providers:CarCloud:TokenEndpoint" http://localhost:5090/oauth/token
   dotnet user-secrets --project src/CheapHours.Host set "Providers:CarCloud:ClientId" cheaphours
   dotnet user-secrets --project src/CheapHours.Host set "Providers:CarCloud:ClientSecret" <client secret>
   dotnet user-secrets --project src/CheapHours.Host set "Providers:CarCloud:PollInterval" 00:00:15
   dotnet user-secrets --project src/CheapHours.Host set "Providers:CarCloud:Accounts:0:Owner" own_demo
   dotnet user-secrets --project src/CheapHours.Host set "Providers:CarCloud:Accounts:0:ExternalAccountId" acct_demo
   dotnet user-secrets --project src/CheapHours.Host set "Providers:CarCloud:Accounts:0:RefreshToken" <seed refresh token>
   ```

   The worker polls only when `Providers:CarCloud` exists. It stores the account on its first start;
   from then on it keeps the refresh tokens the simulator rotates, encrypted with ASP.NET Core Data
   Protection (keys in your user profile), and the seed is never used again. `PollInterval` defaults to
   one minute; 15 seconds makes the demo quicker.

6. **Link the simulated heat-pump cloud.** Set `HEATPUMP_SIGNING_SECRET` in `.env` (at least 32
   characters: `openssl rand -hex 32`, or the charger-password command with 32 bytes) and give the gateway
   the same secret, plus the account the simulator posts for (`acct_demo`, owned by `own_demo`):

   ```sh
   dotnet user-secrets --project src/CheapHours.Host set "Providers:HeatPumpCloud:SigningSecret" <secret>
   dotnet user-secrets --project src/CheapHours.Host set "Providers:HeatPumpCloud:ExpectedReportInterval" 00:00:15
   dotnet user-secrets --project src/CheapHours.Host set "Providers:HeatPumpCloud:Accounts:0:Owner" own_demo
   dotnet user-secrets --project src/CheapHours.Host set "Providers:HeatPumpCloud:Accounts:0:ExternalAccountId" acct_demo
   ```

   The gateway accepts heat-pump webhooks only when `Providers:HeatPumpCloud` exists. The simulator
   posts to `http://host.docker.internal:5081/webhooks/heatpumpcloud/acct_demo` every 15 seconds, so
   the report interval matches; it defaults to one minute.

7. **Start the stack** and wait until the six services with a health check are healthy (the telemetry
   dashboard has none; it just runs):

   ```sh
   docker compose up -d --wait
   ```

8. **Run the gateway, the worker and the API**, each in its own terminal. The charge point connects
   within a few seconds, boots and reports `Available`; the worker logs that it holds the polling lease
   and finds the simulated car on its first poll; the gateway logs each accepted heat-pump webhook:

   ```sh
   dotnet run --project src/CheapHours.Host -- --role gateway --urls http://localhost:5081
   dotnet run --project src/CheapHours.Host -- --role worker
   dotnet run --project src/CheapHours.Host -- --role api --urls http://localhost:5080
   ```

   Health: `/health/live` and `/health/ready` on the gateway (MongoDB and RabbitMQ reachable) and on
   the API (MongoDB reachable, keys seeded).

9. **Charge at the wallbox.** Start a charging session on the simulator; it then sends meter values every 15 seconds,
   which the gateway publishes and the worker stores:

   ```sh
   docker compose exec ocpp-simulator npx tsx admin/v16/Transaction/startTransaction.ts
   docker compose exec ocpp-simulator npx tsx admin/v16/StatusNotification/charging.ts
   docker compose exec mongo mongosh cheaphours --quiet --eval 'db.telemetry.find().sort({ts: -1}).limit(3)'
   docker compose exec mongo mongosh cheaphours --quiet --eval 'db.devices.findOne({}, {lastReading: 1})'
   docker compose exec -e METER_STOP=30000 ocpp-simulator npx tsx admin/v16/Transaction/stopTransaction.ts
   ```

   The simulator does not report `Charging` by itself when a session starts, so the second command
   sends that status. The stop command reports the final meter value in Wh (`METER_STOP`, 2000 when
   not set).

10. **Read it through the API** with the demo key. The device list shows the charger, its status and
   last reading; its telemetry comes in 15-minute buckets (`from` and `to` in UTC, at most 31 days):

   ```sh
   curl -s -H "X-Api-Key: $k" http://localhost:5080/v1/devices
   curl -s -H "X-Api-Key: $k" "http://localhost:5080/v1/devices/<id>/telemetry?from=$(date -u -d '-1 hour' +%FT%TZ)&to=$(date -u +%FT%TZ)"
   ```

   ```powershell
   curl.exe -s -H "X-Api-Key: $k" http://localhost:5080/v1/devices
   $to = [DateTime]::UtcNow; curl.exe -s -H "X-Api-Key: $k" "http://localhost:5080/v1/devices/<id>/telemetry?from=$($to.AddHours(-1).ToString('s'))Z&to=$($to.ToString('s'))Z"
   ```

   A charger that stops reporting for more than three heartbeat intervals is shown as `Offline`, even
   if no gateway is left to record it.

11. **Charge the car.** The device list also shows the simulated car (`Vehicle`, with
    `batteryCapacityWh`). Plug it in and start a session through the simulator's admin endpoints, which
    act as the car's own app; its state of charge and session energy then rise at 11 kW:

    ```sh
    curl -s -X POST http://localhost:5090/admin/vehicles/veh_1/plug-in
    curl -s -X POST http://localhost:5090/admin/vehicles/veh_1/start
    curl -s -H "X-Api-Key: $k" http://localhost:5080/v1/devices
    ```

    Its telemetry, read like the charger's, shows the energy and the last state of charge per 15-minute
    bucket. `POST /admin/vehicles/veh_1/unplug` ends the session.

12. **Watch the heat pump.** The device list also shows the simulated heat pump (`HeatPump`, made by
    Kaltwerk), heating at 3 kW; its telemetry, read like the charger's, shows the energy per 15-minute
    bucket from its lifetime meter. The simulator's admin endpoints change its mode (`Heating`,
    `HotWater`, `Defrost`, `Standby`, `Fault`), list recent deliveries, and redeliver its last event:

    ```sh
    curl -s -X POST http://localhost:5091/admin/heat-pumps/hp_1/mode/Standby
    curl -s http://localhost:5091/admin/deliveries
    curl -s -X POST http://localhost:5091/admin/heat-pumps/hp_1/redeliver
    docker compose exec mongo mongosh cheaphours --quiet --eval 'db.telemetry.aggregate([{$match: {"meta.providerId": "heatpumpcloud"}}, {$group: {_id: "$ts", n: {$sum: 1}}}, {$match: {n: {$gt: 1}}}]).toArray()'
    ```

    The redelivery is signed again but has the same body, so its reading has the same time and its
    telemetry message the same id: the gateway answers `202`, and the worker's inbox drops the duplicate.
    The last command lists heat-pump readings stored twice for one time, and prints `[]`.

    A webhook with a wrong signature is refused with `401` before its body is read:

    ```sh
    curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5081/webhooks/heatpumpcloud/acct_demo \
      -H "Content-Type: application/json" \
      -H "HeatPump-Signature: t=$(date +%s),v1=0000000000000000000000000000000000000000000000000000000000000000" \
      -d '{}'
    ```

    ```powershell
    curl.exe -s -o NUL -w "%{http_code}\n" -X POST http://localhost:5081/webhooks/heatpumpcloud/acct_demo -H "Content-Type: application/json" -H "HeatPump-Signature: t=$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()),v1=$('0' * 64)" -d '{}'
    ```

    `POST /admin/heat-pumps/hp_1/remove` sends `device.deleted`: the heat pump is shown `Offline` and
    kept with its telemetry.

13. **See today's prices.** The worker imports day-ahead prices for `DE-LU` from Energy-Charts when it
    starts (no key needed), then hourly; tomorrow's arrive from about 13:00 Berlin time. Ask for today's
    Berlin day, which has 96 slots (92 or 100 on the daylight-saving days). The bash lines need GNU `date`
    with time-zone data (Linux); Git Bash on Windows has none and silently uses UTC, so use the PowerShell
    lines there:

    ```sh
    day=$(TZ=Europe/Berlin date +%F)
    from=$(date -u -d "TZ=\"Europe/Berlin\" $day 00:00" +%FT%TZ)
    to=$(date -u -d "TZ=\"Europe/Berlin\" $day 00:00 +1 day" +%FT%TZ)
    curl -s -H "X-Api-Key: $k" "http://localhost:5080/v1/prices?zone=DE-LU&from=$from&to=$to&limit=200"
    ```

    ```powershell
    $tz = [TimeZoneInfo]::FindSystemTimeZoneById('W. Europe Standard Time')
    $day = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::UtcNow, $tz).Date
    $from = [TimeZoneInfo]::ConvertTimeToUtc($day, $tz).ToString('s') + 'Z'
    $to = [TimeZoneInfo]::ConvertTimeToUtc($day.AddDays(1), $tz).ToString('s') + 'Z'
    curl.exe -s -H "X-Api-Key: $k" "http://localhost:5080/v1/prices?zone=DE-LU&from=$from&to=$to&limit=200"
    ```

14. **Set a goal and read its plan.** Take the car's and the wallbox's ids from the device list, and pick
    a target above the car's current charge and a departure within 48 hours. The answer is `201` with a
    draft plan: the cheapest 15-minute slots before the departure minus 30 minutes, what they cost, what
    charging straight away would cost, and when the draft expires (10 minutes). Repeating the request
    with the same `Idempotency-Key` returns the same answer; `PATCH` changes the goal and makes a new
    draft version:

    ```sh
    car=dev_...; wallbox=dev_...
    departure=$(date -u -d 'tomorrow 06:00' +%FT%TZ)
    curl -s -X POST http://localhost:5080/v1/goals -H "X-Api-Key: $k" -H "Content-Type: application/json" \
      -H "Idempotency-Key: demo-goal-1" \
      -d "{\"vehicleDeviceId\":\"$car\",\"chargerDeviceId\":\"$wallbox\",\"targetSocPercent\":90,\"departureAt\":\"$departure\"}"
    curl -s -H "X-Api-Key: $k" http://localhost:5080/v1/goals/<goal id>/plan
    curl -s -X PATCH http://localhost:5080/v1/goals/<goal id> -H "X-Api-Key: $k" -H "Content-Type: application/json" -d '{"targetSocPercent":95}'
    ```

    ```powershell
    $car = 'dev_...'; $wallbox = 'dev_...'
    $departure = [DateTime]::UtcNow.Date.AddDays(1).AddHours(6).ToString('s') + 'Z'
    $body = @{ vehicleDeviceId = $car; chargerDeviceId = $wallbox; targetSocPercent = 90; departureAt = $departure } | ConvertTo-Json -Compress
    Invoke-RestMethod -Method Post http://localhost:5080/v1/goals -Headers @{ 'X-Api-Key' = $k; 'Idempotency-Key' = 'demo-goal-1' } -ContentType 'application/json' -Body $body
    Invoke-RestMethod http://localhost:5080/v1/goals/<goal id>/plan -Headers @{ 'X-Api-Key' = $k }
    ```

    A goal set before tomorrow's prices are published charges in the slots that have a price; when the
    worker announces tomorrow's prices, it re-plans the goal into the cheaper night as a new version.
    `GET /v1/savings?from=...&to=...` counts confirmed plans only, so it shows zeros until a plan is
    confirmed.

15. **Confirm the plan and follow its commands.** Confirming the draft (key with the `execute` scope)
    answers `202` with the confirmed plan and one start and one stop per run of slots, each `Pending` and
    due at its slot. Follow a command at `/v1/commands/{id}`: the worker publishes it when it is due, sends
    it to the wallbox through the gateway, and marks it `Confirmed` only when the wallbox reports the new
    state. A command can also go straight to a device:

    ```sh
    curl -s -X POST http://localhost:5080/v1/plans/<plan id>/confirm -H "X-Api-Key: $k" -H "Idempotency-Key: demo-confirm-1"
    curl -s -H "X-Api-Key: $k" http://localhost:5080/v1/commands/<command id>
    curl -s -X POST http://localhost:5080/v1/devices/$wallbox/commands -H "X-Api-Key: $k" -H "Content-Type: application/json" \
      -H "Idempotency-Key: demo-start-1" -d '{"type":"StartCharging"}'
    ```

    ```powershell
    Invoke-RestMethod -Method Post http://localhost:5080/v1/plans/<plan id>/confirm -Headers @{ 'X-Api-Key' = $k; 'Idempotency-Key' = 'demo-confirm-1' }
    Invoke-RestMethod http://localhost:5080/v1/commands/<command id> -Headers @{ 'X-Api-Key' = $k }
    Invoke-RestMethod -Method Post "http://localhost:5080/v1/devices/$wallbox/commands" -Headers @{ 'X-Api-Key' = $k; 'Idempotency-Key' = 'demo-start-1' } -ContentType 'application/json' -Body '{"type":"StartCharging"}'
    ```

    The direct start becomes `Sent` once the wallbox accepts the remote start, and `Confirmed` a moment
    later: on a remote start the simulator starts a transaction and reports `Charging` by itself (unlike
    the admin script in step 9). Restarting the simulator mid-command (`docker compose restart
    ocpp-simulator`) still ends with one session: the simulator comes back `Available` without its
    transaction, and after 60 seconds without confirmation the worker reads that state and resends with
    the same key; the gateway never starts a second session for a wallbox that is charging. The pinned simulator accepts `SetPowerLimit` without changing its power, so a power limit
    below its simulated power is never confirmed and is dead-lettered after reconciling; plans never use
    power limits. `docs/reliability.md` maps every failure case to the test that proves it.

16. **Re-link the car-cloud account.** Revoking it (`curl -s -X POST
    http://localhost:5090/admin/accounts/acct_demo/revoke`) makes the next poll fail with `invalid_grant`:
    the worker logs that `carcloud` account `acct_demo` needs to be linked again and stops polling it. The
    simulator also forgets rotated tokens when it restarts, which looks the same to the worker. To link
    the account again, restart the simulator (`docker compose restart car-cloud`), which accepts its
    configured seed once more, then restart the worker, which gives the marked account that seed again.

17. **Observe a command.** The stack's Aspire dashboard (<http://localhost:18888>) receives the traces, metrics
    and logs of every role started with `OTEL_EXPORTER_OTLP_ENDPOINT`; without it, nothing is exported. Restart
    the three roles from step 8 with the variable set:

    ```sh
    export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
    dotnet run --project src/CheapHours.Host -- --role gateway --urls http://localhost:5081
    dotnet run --project src/CheapHours.Host -- --role worker
    dotnet run --project src/CheapHours.Host -- --role api --urls http://localhost:5080
    ```

    ```powershell
    $env:OTEL_EXPORTER_OTLP_ENDPOINT = 'http://localhost:4317'   # in each of the three terminals
    ```

    Post a direct `StartCharging` for the wallbox (step 15) and note the command id. In the dashboard:

    - **Structured logs**: filter on `commandId` = the command id; every entry about the command carries it,
      with `deviceId` and `providerId`, and links to its trace.
    - **Traces**: the command's trace holds spans from `cheaphours-api` (the `POST`), `cheaphours-worker`
      (the outbox publish, `dispatch StartCharging`, the result and `command confirm`) and `cheaphours-gateway`
      (the command's handling and the `ocpp RemoteStartTransaction` call). Each charger frame, poll and price
      import is a trace of its own.
    - **Metrics**: `cheaphours.provider.requests` and `cheaphours.provider.request.duration` (vendor and OCPP
      calls), `cheaphours.provider.circuit.state`, `cheaphours.command.confirmation.duration`,
      `cheaphours.command.dead_lettered`, `cheaphours.outbox.lag`, `cheaphours.gateway.connections` and
      `cheaphours.telemetry.readings`, each tagged by provider, never by device.

    Outside Development the console logs are one JSON object per line, with the trace and span ids.

18. **Open the dashboard.** With the stack up (step 7) and the three roles running (step 8, the API on
    `--urls http://localhost:5080`), open <http://localhost:5070>. The `web` service serves the page and
    forwards every `/v1` request to the API, so the page and the API share one origin. Enter a key: it stays
    in that browser tab only and travels only in the `X-Api-Key` header. The page shows the devices, today's
    and tomorrow's prices (Berlin days), the goals with a form to set one, a goal's plan on the prices, and
    the savings of the last 30 days.

    Everything but confirming works with a `read` key. To see that, add a `read`-only key next to the demo
    key (make it like the demo key in step 4, then restart the API):

    ```sh
    dotnet user-secrets --project src/CheapHours.Host set "ApiKeys:Keys:1:Name" demo-read
    dotnet user-secrets --project src/CheapHours.Host set "ApiKeys:Keys:1:Hash" <hash of the read key>
    dotnet user-secrets --project src/CheapHours.Host set "ApiKeys:Keys:1:Owner" own_demo
    dotnet user-secrets --project src/CheapHours.Host set "ApiKeys:Keys:1:Scopes:0" read
    ```

    With the `read` key, confirming a draft says an `execute` key is needed and the plan stays a draft.
    "Forget key" removes it; with the demo key, confirming lists the new commands and follows each one until
    it is final. With the API stopped, each section says the API is unreachable.

`docker compose down -v` stops everything and deletes the local data.

## Documentation

- [AGENTS.md](AGENTS.md): conventions, boundaries, and what is built and what is left
- [Decision records](docs/adr/README.md): why it is built this way, and what was rejected
- [OpenAPI contract](docs/api/openapi.v1.json): the committed `/v1` contract; in Development the API also serves an interactive reference at `/scalar/v1`

## Licence and data

Price data: [Energy-Charts](https://api.energy-charts.info/), Fraunhofer ISE, licensed CC BY 4.0 (from
Bundesnetzagentur | SMARD.de). The recorded responses in `tests/CheapHours.UnitTests/Prices/Fixtures/`
are kept unchanged, with their `license_info`.
