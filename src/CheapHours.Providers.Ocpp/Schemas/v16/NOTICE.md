# OCPP 1.6 JSON schemas

These files are the OCPP 1.6 JSON schemas (draft-04) published by the
[Open Charge Alliance](https://openchargealliance.org/), with the OCA's errata v4 applied. They are
© Open Charge Alliance and licensed under
[Creative Commons Attribution-NoDerivatives 4.0](https://creativecommons.org/licenses/by-nd/4.0/).
They are redistributed **unchanged**; do not edit them. `OcppSchemaFileTests` checks their SHA-256.

They were copied from the [mobilityhouse/ocpp](https://github.com/mobilityhouse/ocpp) library (MIT),
path `ocpp/v16/schemas/`, commit `aead68e6a2877ba712810525f1ca85ce8db59783`.

Only the schemas for the messages the gateway handles are included (each with its `Response`):
`BootNotification`, `Heartbeat`, `StatusNotification`, `MeterValues`, `StartTransaction` and
`StopTransaction`. Add others the same way, unchanged.
