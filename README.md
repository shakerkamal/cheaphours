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
- Docker (for MongoDB, RabbitMQ and the simulators, once the local environment lands)

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

## Documentation

- [AGENTS.md](AGENTS.md): conventions, boundaries and live progress
- [Decision records](docs/adr/README.md): why it is built this way, and what was rejected
- [OpenAPI contract](docs/api/openapi.v1.json): the committed `/v1` contract; in Development the API also serves an interactive reference at `/scalar/v1`

## Licence and data

Price data: [Energy-Charts](https://api.energy-charts.info/), Fraunhofer ISE, licensed CC BY 4.0.
