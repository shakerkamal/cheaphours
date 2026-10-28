# ADR-002: Store telemetry in a MongoDB time-series collection

- **Status:** Accepted
- **Date:** 2026-10-26
- **Decision ids:** none

## Context

Every device reports readings: energy (Wh), power (W) and state of charge (%), each with the device's
own timestamp.
- The design target is 1,000 devices reporting every 30 seconds, about 33 readings a second, or roughly
  85 million readings in 30 days.
- The dashboard reads them back as 15-minute buckets.
- The planner needs each device's latest reading.
- Readings older than 30 days have no use.
- MongoDB already holds every other collection, and runs as a replica set for the outbox's transactions.

## Decision

- Readings go into one **time-series collection**, `telemetry`:
  - time field `ts`;
  - meta field `meta`, holding the device id and provider id;
  - granularity `seconds`;
  - `expireAfterSeconds` of 30 days;
  - a secondary index on `meta.deviceId` and `ts`.
- A reading stores only what the device reported: missing power or state of charge is absent, never zero.
- Each document carries the id of the message that delivered it.
- The latest reading is kept separately on the device (`devices.lastReading`), and only moves forward in
  time.
- Startup creates the collection, and fails if a collection of that name exists but is not time-series.
  A different retention is corrected in place.

## Alternatives considered

- **A separate time-series database (InfluxDB, TimescaleDB).** Rejected: a second store to run, back up
  and secure, for a volume MongoDB's bucketed storage handles. Its query language would also be a second
  one for the read API.
- **An ordinary collection with a TTL index.** Rejected: roughly ten times the storage and index size at
  this volume, because readings are not bucketed and compressed.
- **Pre-aggregated 15-minute buckets written by the consumer.** Postponed rather than rejected: the read API
  aggregates on read first and measures. Buckets can be added later without changing how readings are
  stored.

## Consequences

- **No transactions or unique indexes.** Time-series collections allow neither, so exactly-once storage
  cannot come from either. Instead:
  - the consumer's inbox drops handled message ids;
  - a redelivered message first deletes readings carrying its id, narrowed by device and time range so the
    delete uses the index;
  - the inbox entry is written last.
- **Deletes need MongoDB 7.0.** They are by a non-meta field, which the compose file and the test
  container both satisfy by pinning 8.0.32.
- **Late readings are fine.** Out-of-order readings are stored as they come: the collection is ordered
  by `ts`, and `lastReading` compares timestamps.
- **Tests to keep:** the collection options at startup, the wrong-kind failure, a duplicate message, and
  a redelivery after a partial write.
