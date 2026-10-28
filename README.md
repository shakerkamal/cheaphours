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

## Build and test

```sh
dotnet build
dotnet test
```

The build treats every warning as an error. Tests run on Microsoft.Testing.Platform (xUnit v3).

## Run

One executable, three roles:

```sh
dotnet run --project src/CheapHours.Host -- --role api
dotnet run --project src/CheapHours.Host -- --role gateway
dotnet run --project src/CheapHours.Host -- --role worker
```

## Run locally

The local stack is MongoDB (a single-node replica set), RabbitMQ and an OCPP 1.6 charge-point simulator.
The simulator connects to the gateway running on your machine.

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

5. **Start the stack** and wait until all three services are healthy:

   ```sh
   docker compose up -d --wait
   ```

6. **Run the gateway, the worker and the API**, each in its own terminal. The simulator connects within
   a few seconds, boots and reports `Available`:

   ```sh
   dotnet run --project src/CheapHours.Host -- --role gateway --urls http://localhost:5081
   dotnet run --project src/CheapHours.Host -- --role worker
   dotnet run --project src/CheapHours.Host -- --role api --urls http://localhost:5080
   ```

   Health: `/health/live` and `/health/ready` on the gateway (MongoDB and RabbitMQ reachable) and on
   the API (MongoDB reachable, keys seeded).

7. **Charge.** Start a charging session on the simulator; it then sends meter values every 15 seconds,
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

8. **Read it through the API** with the demo key. The device list shows the charger, its status and
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

`docker compose down -v` stops everything and deletes the local data.

## Documentation

- [AGENTS.md](AGENTS.md): conventions, boundaries, and what is built and what is left
- [Decision records](docs/adr/README.md): why it is built this way, and what was rejected
- [OpenAPI contract](docs/api/openapi.v1.json): the committed `/v1` contract; in Development the API also serves an interactive reference at `/scalar/v1`

## Licence and data

Price data: [Energy-Charts](https://api.energy-charts.info/), Fraunhofer ISE, licensed CC BY 4.0.
