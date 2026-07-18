# Production audio playback contract

This contract separates media readiness from `Track.Status`, which remains the
commerce-availability field. R2 remains private; browser playback is always proxied
through ASP.NET Core.

## Request flow

```text
GET /api/v1/tracks/{trackId}/playback
  -> optional listener identity
  -> shared visibility policy
  -> TrackMedia.State == Ready
  -> freshness validation when required
  -> short-lived HMAC application ticket
  -> /stream/{trackId}/audio?ticket=...
  -> forwarded R2 Range request
```

The playback-info response and ticket-bearing stream response are `private,
no-store`. Tickets contain only the track ID, playback scope, issued/expiry times,
random ticket ID, and an optional short-lived private-track grant. Object keys,
emails, IP addresses, and R2 credentials are never ticket claims.

`GET` and `HEAD /stream/{trackId}/audio` validate the ticket when enforcement is
enabled. GET forwards Range to storage and preserves `200`, `206`, content length,
content range, and `416 bytes */TOTAL`. HEAD returns the same metadata without a
body. The temporary legacy public path is controlled by:

```text
PlaybackMedia__ReadinessEnforcementEnabled=false
PlaybackMedia__LegacyPublicStreamEnabled=true
```

Do not enable enforcement until reconciliation reports zero unresolved published
track failures and the ticket-aware frontend is deployed.

## Media lifecycle

`TrackMedia.State` is one of:

```text
Draft Uploading Uploaded Processing Validating Ready Failed Quarantined Deleted
```

Only `IMediaStateMachine` performs transitions. `Deleted` is terminal. Legacy rows
are initialized by application reconciliation, not migration SQL: recognizable
stable keys become `Uploaded`; empty locations become `Draft`; absolute, signed, or
proxy locations become `Failed` with `legacy_location_unrecognized`. No backfill
path marks a row `Ready` without production-path validation.

New uploads remain hidden as `Uploaded`. Creator and admin publication paths require
`Ready`; restore operations fall back to hidden when media is not Ready.

## Validation and reconciliation

Validation checks storage metadata, nonzero/matching size, supported MIME type,
SHA-256 when known, parsed duration, an ffmpeg decode probe, first-byte range
semantics, and a signed internal probe through the configured production playback
host. The internal probe signature is short-lived and is not a browser credential.

Admin operations:

```text
POST /admin/media-reconciliation/runs
GET  /admin/media-reconciliation/runs
GET  /admin/media-reconciliation/runs/{runId}
```

Reports persist immutable findings but never return object keys in response DTOs.
Safe remediation can validate/promote `Uploaded` rows or demote a drifted `Ready`
row. It never deletes an orphan, guesses a key mapping, overwrites an object, or
promotes unverifiable media.

The in-process worker creates a DI scope per run and is disabled by default. Enable
it only after production storage, ffmpeg, production probe, and operational timing
are verified:

```text
PlaybackMedia__ReconciliationWorkerEnabled=true
PlaybackMedia__ReconciliationIntervalMinutes=60
```

For scans that must survive deployments, invoke the same service from the durable
worker/queue instead of relying only on the in-process timer.

## Required production configuration

```text
PlaybackMedia__TicketSigningKey=<at least 32 characters, secret store>
PlaybackMedia__ProductionProbeSigningKey=<different 32+ character secret>
PlaybackMedia__ProductionPlaybackBaseUrl=https://cambrian-backend-api.onrender.com
PlaybackMedia__BackendRelease=<deployment commit/release ID>
```

Production startup fails closed when either signing key or the absolute production
base URL is missing. Do not commit either key.

## Browser telemetry

`POST /api/v1/playback/telemetry` accepts only the documented media event enum and a
bounded typed batch. Unknown properties, full URLs, query strings, ticket-shaped
session IDs, and invalid media/status enums are rejected. The endpoint is rate
limited and records host-only media routing data; signed locations are never logged.

## Rollout gate

Migration `20260716201351_ProductionAudioPlaybackHardening` creates `TrackMedia`,
`MediaReconciliationRuns`, and `MediaReconciliationFindings` with a non-unique
object-key index. It does not alter `Track.Status`, backfill rows, store tickets, or
enable enforcement.

Before enabling enforcement, save evidence for: migration deployment; reconciliation
run ID with zero unresolved published failures; anonymous public and owner/private
behavior; full, HEAD, range, and 416 headers; backend Release tests; and browser
time advancement, pause/resume, and seek across the required engine/device matrix.
