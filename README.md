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

3. **Start the stack** and wait until all three services are healthy:

   ```sh
   docker compose up -d --wait
   ```

4. **Run the gateway.** The simulator connects within a few seconds, boots and reports `Available`:

   ```sh
   dotnet run --project src/CheapHours.Host -- --role gateway --urls http://localhost:5081
   ```

   Health: `http://localhost:5081/health/live` and `http://localhost:5081/health/ready`.

`docker compose down -v` stops everything and deletes the local data.

## Documentation

- [AGENTS.md](AGENTS.md): conventions, boundaries, and what is built and what is left
- [Decision records](docs/adr/README.md): why it is built this way, and what was rejected
- [OpenAPI contract](docs/api/openapi.v1.json): the committed `/v1` contract; in Development the API also serves an interactive reference at `/scalar/v1`

## Licence and data

Price data: [Energy-Charts](https://api.energy-charts.info/), Fraunhofer ISE, licensed CC BY 4.0.
