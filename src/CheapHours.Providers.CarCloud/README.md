# Car-cloud provider (`carcloud`)

Connects electric vehicles through the car maker's cloud. The worker polls each linked account, and every
vehicle becomes a `Vehicle` device with its battery capacity, status, state of charge and session energy.
Start and stop are carried out with the vendor's idempotency key. The local stand-in for the vendor is
`simulators/CheapHours.Simulators.CarCloud`.

Capabilities: `ReadTelemetry`, `ReadStateOfCharge`, `StartStop`.

## Vendor API

| Call | Request | Answer |
|---|---|---|
| Token | `POST /oauth/token`, form `grant_type=refresh_token`, `refresh_token`, `client_id`, `client_secret` | `{access_token, token_type, expires_in, refresh_token}`; 400 `{error:"invalid_grant"}`; 401 `{error:"invalid_client"}` |
| Vehicles | `GET /api/v1/vehicles` | `{vehicles:[{id, make, model, batteryCapacityKwh}]}` |
| State | `GET /api/v1/vehicles/{id}/state` | `{timestamp, online, pluggedIn, chargingState, socPercent, chargePowerKw?, sessionEnergyKwh}`; 404 when the vehicle is gone |
| Start, stop | `POST /api/v1/vehicles/{id}/charging/start` or `/stop`, header `Idempotency-Key` | 202 `{requestId}`; 422 `{error:"not_plugged_in"}`; 400 without a key |

`chargingState` is `Charging`, `Stopped`, `Complete` or `Unplugged`.

## Authentication

- OAuth 2.0 refresh-token grant with `client_secret_post`. The adapter kit refreshes the access token 60 s
  before it expires or after a 401, once per account at a time, and retries the call once.
- The vendor rotates refresh tokens. The new token is stored, encrypted with ASP.NET Core Data Protection,
  before the new access token is used.
- Tokens and the client secret are never logged, put in errors or results, or stored in clear.

## Configuration

Section `Providers:CarCloud` (all keys from the kit's `OAuthProviderOptions`):

| Key | Default | Notes |
|---|---|---|
| `BaseAddress`, `TokenEndpoint` | | Required, absolute URIs |
| `ClientId`, `ClientSecret` | | Required; the secret goes in user-secrets |
| `PollInterval` | `00:01:00` | At least 5 s; also the devices' report interval |
| `RateLimit:RequestsPerMinute` | 30 | Per account |
| `Http:*` | see the kit | Timeouts, retries, circuit breaker |
| `Accounts:n:Owner`, `ExternalAccountId`, `RefreshToken` | | One account per owner; the seed refresh token goes in user-secrets |

Startup fails when a required key is missing; the error names the key and never shows a value.

## Limits and failures

- Calls per account are kept under `RateLimit:RequestsPerMinute`. A vendor 429 pauses the account for its
  `Retry-After`; the pause is stored, so a new polling leader honours it.
- A 429, an open circuit or a revoked token fails the whole poll. A 5xx or timeout on one vehicle's state
  leaves that vehicle out of the poll and is logged; the others are reported.

## Quirks

- Energy is the energy added **in the current session**. It restarts at zero with each session; the telemetry
  pipeline treats the drop as a meter reset.
- A vehicle that is asleep or unreachable (`online: false`) is reported `Offline` with no reading.
- A state timestamp without an offset is not trusted: that vehicle gets a `MalformedInput` event and the
  others are still reported.
- A vehicle whose state answers 404 is reported as removed (`Offline`, `removedAt` set) and kept with its
  telemetry. It counts as present again when the vendor lists it once more.
- The device's hardware needs a make and a model; a vehicle without them is reported without hardware.

## Not supported

- `SetPowerLimit` is refused with `capability-not-supported` before any call. A power limit on a start
  command is ignored.
- There is no account-linking flow or consent screen (D3: one seeded demo owner). An account is seeded from
  configuration with a refresh token from a consent done once.

## Re-linking an account

When the vendor answers `invalid_grant` (the account was revoked, or the stored refresh token was lost), the
account is marked reconnect-needed and polling it stops; the worker log names the provider and the account.
To link it again, put a new seed refresh token in `Providers:CarCloud:Accounts:n:RefreshToken` and restart
the worker: seeding replaces the tokens of an account marked reconnect-needed and polls it at once. A working
account is never overwritten by the configured seed.
