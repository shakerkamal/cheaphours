# Heat-pump-cloud provider (`heatpumpcloud`)

Connects heat pumps through their maker's cloud, which posts signed webhooks to the gateway. Every heat pump
becomes a `HeatPump` device with its maker and model, status, electrical power and lifetime energy. The
provider checks each webhook's signature (the first `IWebhookVerifier`, D6) and translates the vendor's
events; it does not poll and sends no commands. The local stand-in for the vendor is
`simulators/CheapHours.Simulators.HeatPumpCloud`.

Capabilities: `ReadTelemetry`, `PushesEvents`.

## Callback URL

The vendor is given one callback URL per linked account:

```
POST https://<gateway>/webhooks/heatpumpcloud/<externalAccountId>
```

The path names the account, and the account names its owner (configured below). Every heat pump a webhook
creates belongs to that owner. A webhook for an account that is not configured gets `404`.

## Signing

Every webhook carries:

| Header | Value |
|---|---|
| `Content-Type` | `application/json` |
| `HeatPump-Event-Id` | `evt_…`, the vendor's event id |
| `HeatPump-Signature` | `t=<unix seconds>,v1=<64 hex>` |

`v1` is HMAC-SHA256 with the signing secret over the ASCII timestamp, a `.` and the raw body bytes:
`HMAC(secret, "<t>.<body>")`. A webhook verifies only when:

- the header has exactly one numeric `t` and at least one `v1` of 64 hex digits (other keys are ignored, so
  the vendor can send several `v1` values while it rotates a secret);
- one `v1` equals the computed HMAC, compared in constant time;
- `t` is within `SignatureTolerance` of the time the gateway received the webhook, in either direction.

Anything else, including a missing or garbled header, fails verification without an exception, and the
gateway answers `401` before the body is read as JSON.

## Event types

```json
{"eventId":"evt_01","type":"device.state","occurredAt":"2026-11-02T10:00:00Z","accountId":"acct_demo",
 "device":{"id":"hp_1","maker":"Kaltwerk","model":"AeroHeat 9"},
 "state":{"online":true,"mode":"Heating","powerKw":2.4,"energyKwh":1234.5678}}
```

| Type | Events |
|---|---|
| `device.state` | `Discovered` (type `HeatPump`), `Connected` with maker and model, `StatusChanged`, and a reading of `powerKw` in W and `energyKwh` in Wh, all at `occurredAt` |
| `device.deleted` (`device.id` only) | `DeviceRemoved` at `occurredAt`; the device is marked `Offline` with `removedAt` and kept |
| anything else (`account.updated`, …) | none; the webhook still gets `202` |

| `mode` | Status |
|---|---|
| `Heating`, `HotWater`, `Defrost`, `Standby` | `Available` |
| `Fault` | `Faulted` |
| any other value | `Unknown` |
| (`online: false`) | `Offline`, with no reading |

A body that is not JSON, has no `type`, lacks `device.id`, `state`, `mode`, `powerKw` or `energyKwh`, has
negative or out-of-range values, or whose `occurredAt` has no offset becomes one `MalformedInput`, and the
gateway answers `400`.

## Configuration

Section `Providers:HeatPumpCloud` (all keys from the kit's `WebhookProviderOptions`), on the gateway:

| Key | Default | Notes |
|---|---|---|
| `SigningSecret` | | Required, at least 32 characters; goes in user-secrets |
| `SignatureTolerance` | `00:05:00` | 1 s – 15 min |
| `ExpectedReportInterval` | `00:01:00` | At least 5 s; the devices' report interval, so a heat pump silent for three intervals shows `Offline` |
| `Accounts:n:Owner`, `Accounts:n:ExternalAccountId` | | One callback per account; account ids are unique |

Startup fails when a value is missing or invalid; the error names the key and never shows a value. Without
the section the provider is not registered and its webhooks get `404`.

## Quirks

- Energy is the heat pump's **lifetime** meter, so it only grows.
- The vendor reports no state of charge; readings have none.
- An offline heat pump reports no reading.
- The vendor delivers at least once. A redelivered event has the same body, so its reading has the same
  time and the telemetry message the same id: the worker's inbox stores it once. A vendor that changed the
  state's timestamp on a retry would add a second reading, at that new time.

## Not supported

- Commands (modes, setpoints).
- Rotating the signing secret or per-account secrets: one secret per provider, from configuration. Several
  `v1` values in one header are accepted, which is how a vendor would rotate.
- A subscription API and an account-linking flow: accounts and the callback URL are set up once, by hand
  (D3: one demo owner).
- Replay protection beyond the timestamp window: duplicates are made harmless, not refused.
