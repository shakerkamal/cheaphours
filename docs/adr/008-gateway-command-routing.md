# ADR-008: Route charger commands through a per-replica gateway queue

- **Status:** Accepted
- **Date:** 2026-10-16
- **Decision ids:** D1

## Context

The worker's dispatcher sends commands through `ICommandSender`. Vendor-cloud providers send over HTTP, but
an OCPP charger is reachable only over the WebSocket it opened to a **specific gateway replica**. The
worker cannot write to that socket, and with more than one gateway replica, any replica might hold it.

The command path must keep the system's guarantees: no command lost across restarts of the worker,
gateway or broker; none applied twice; and every command traceable from the API call to the charger's
reply.

## Decision

- When a charger connects, the gateway records `chargePointId → replicaId` in a **connection registry**
  in MongoDB, and clears or overwrites the entry when the socket closes or the charger reconnects
  elsewhere.
- Each gateway replica consumes its own queue, **`gateway.{replicaId}.commands`**.
- The OCPP provider's `ICommandSender` looks up the replica and publishes a `GatewayCommand` (charge point
  id, replica id, the command and its idempotency key) to that replica's queue. The routing key is
  `GatewayCommand.DestinationFor(replicaId)` in `CheapHours.Messaging.Contracts`.
- The replica sends the OCPP `Call`, waits for the `CallResult` or `CallError`, and publishes a
  `GatewayCommandResult` correlated by command id. The worker updates the command from it.
- If no replica holds the charger, the send is `Transient` and the retry policy applies. The charger's
  next `BootNotification` triggers a reconcile of its open commands.

## Alternatives considered

- **The gateway polls MongoDB for commands for the chargers it holds.** Rejected: it adds polling latency
  and database load, and needs its own locking to stop two replicas picking up the same command.
- **The worker calls the gateway over HTTP, with sticky routing by charge point id.** Rejected: it needs
  ingress-level affinity, loses commands if the gateway is restarting at that moment, and adds a second
  delivery path next to RabbitMQ with its own retry logic.
- **One shared `gateway.commands` queue for all replicas.** Rejected: a replica that does not hold the
  socket would have to requeue the message, which loops under load and reorders commands.

## Consequences

- Commands for OCPP chargers use the same broker, outbox, retries and dead-lettering as everything else.
- A stale registry entry, for example after a gateway crash, can send a command to the wrong queue. It
  then times out and is reconciled, and the idempotency key makes the resend safe. A fault test checks this by
  killing the simulator mid-command.
- The demo runs one gateway replica, but the routing is already correct for several.
- The OCPP provider needs an abstraction for the registry lookup and publish that the kit provides
  without referencing Persistence or Messaging. This is designed with command dispatch.
