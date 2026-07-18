# Qualified Play and The Scene Backend Contract

Effective: 2026-07-13

This document is the frontend and operations contract for Cambrian's authoritative
play-count and ranking pipeline. PostgreSQL is the source of truth. PostHog, API
caches, and chart snapshots are derived projections only.

## Authority and historical boundary

- `QualifiedPlayEvents` is the append-only ledger for plays accepted after this
  feature is deployed.
- `TrackStats.QualifiedPlayCount` is the synchronous ledger projection.
- `TrackStats.LegacyPlayCount` is a frozen migration-time baseline for historical
  counts that cannot be requalified from old `StreamSessions`.
- `TrackStats.PlayCount = LegacyPlayCount + QualifiedPlayCount` is the lifetime
  count returned by public and creator APIs.
- Historical `StreamSessions` are labeled `legacy_unqualified`. The migration does
  not fabricate qualified events from them.
- `StreamSessions` remains the in-progress playback lifecycle; a start is not a play.

## Qualification rules

The server-calculated threshold is `min(30 seconds, 50% of known track duration)`.
An unknown or invalid duration uses 30 seconds. Duration accepts seconds or `mm:ss`
/ `hh:mm:ss`.

- `activePlaybackSeconds` describes active listening since the latest start/resume.
  It is capped by server-observed elapsed time and accumulated across resumes.
- `pausedSeconds`, when supplied, is subtracted from server-observed elapsed time
  and caps an explicit active-time claim as well.
- If neither active nor paused evidence is supplied, the segment contributes zero.
- `endingPositionSeconds` and `seekCount` never add active time. Seeking past the
  threshold does not qualify.
- The owner previewing their own track never qualifies.
- recognized crawler/bot user agents never create a qualifying session.
- A track must be public, status `available` or `active`, not deleted/purge-pending/
  purged/exclusive-sold, and have a nonblank audio key at both start and stop.
- All decisions and windows use UTC.

The rolling listener/track deduplication window defaults to 60 minutes and is
configured with `Playback:DeduplicationWindowMinutes`. The lower boundary is
inclusive: a replay qualifies only after the prior qualified event is more than the
configured window old.

Authenticated plays retain the platform user ID and also use a SHA-256 listener key
for deduplication. Anonymous session IDs use only an HMAC-SHA-256 hash. Raw IP
addresses and raw anonymous identifiers are never stored.

## Playback endpoints

### `POST /stream/start`

Authentication is optional.

Headers:

- `Authorization: Bearer ...` for authenticated listeners.
- `X-Cambrian-Anonymous-Session: <stable opaque value>` for anonymous non-browser
  clients, maximum 256 characters. Browser clients without this header receive the
  HttpOnly `cambrian_playback_session` cookie and must retain cookies.
- `Idempotency-Key: <opaque value>` is optional, maximum 128 characters. The JSON
  `clientEventId` is the fallback when the header is absent.

Request:

```json
{
  "trackId": "00000000-0000-0000-0000-000000000000",
  "clientEventId": "optional-start-delivery-id"
}
```

Successful response (`200`):

```json
{
  "success": true,
  "data": {
    "streamId": "00000000-0000-0000-0000-000000000000",
    "status": "started",
    "qualificationThresholdSeconds": 30,
    "deduplicationWindowMinutes": 60,
    "serverTimeUtc": "2026-07-13T12:00:00Z",
    "anonymousSessionAccepted": false
  }
}
```

Start statuses are `started`, `resumed`, `already_started`, `already_counted`,
`owner_preview`, `ineligible`, or `bot`. `bot` has a null `streamId`. A start never
increments a count.

### `POST /stream/stop`

Authentication is optional, but the caller must own the playback session. Anonymous
callers must send the same anonymous header or cookie used at start.

Headers use the same rules as start. When omitted, stop idempotency is derived from
the playback-session ID.

Request:

```json
{
  "streamId": "00000000-0000-0000-0000-000000000000",
  "activePlaybackSeconds": 30,
  "pausedSeconds": null,
  "seekCount": 0,
  "endingPositionSeconds": 30,
  "clientEventId": "optional-stop-delivery-id"
}
```

`activePlaybackSeconds` and `pausedSeconds` are segment values since the latest
start/resume. When both are present, accepted time is the lesser of reported active
time and server elapsed time minus pauses. Values must be nonnegative and no greater
than the configured maximum (21,600 seconds by default). A stop closes the segment;
another segment must begin with `/stream/start`. Retrying a closed stop cannot add
its evidence again, even with a different idempotency key.

Successful response (`200`):

```json
{
  "success": true,
  "data": {
    "streamId": "00000000-0000-0000-0000-000000000000",
    "status": "qualified",
    "qualified": true,
    "counted": true,
    "idempotentReplay": false,
    "activePlaybackSeconds": 30,
    "qualificationThresholdSeconds": 30,
    "qualifiedAtUtc": "2026-07-13T12:00:30Z",
    "lifetimePlayCount": 42,
    "serverTimeUtc": "2026-07-13T12:00:30Z"
  }
}
```

Stop statuses are `pending`, `qualified`, `deduplicated`, `owner_preview`,
`ineligible`, or `legacy_unqualified`.

- First durable acceptance: `qualified=true`, `counted=true`.
- Same delivery retried: `qualified=true`, `counted=false`,
  `idempotentReplay=true`; it returns the original event timestamp.
- Different session inside the rolling listener window: `qualified=true`,
  `counted=false`, `status=deduplicated`.
- Repeated stop for an already-closed below-threshold/rejected segment:
  `counted=false`, `idempotentReplay=true`, with no time added.
- Below threshold/rejected: `qualified=false`, `counted=false`.

The event insert, session state, and lifetime projection update commit in one
database transaction. A PostgreSQL unique constraint covers both the normalized
idempotency key and playback-session ID. PostgreSQL advisory locks serialize rolling
deduplication and aggregate writes across backend instances.

### Structured playback errors

Playback errors use:

```json
{
  "success": false,
  "error": {
    "code": "invalid_track_id",
    "message": "trackId must be a valid GUID.",
    "correlationId": "request-trace-id"
  }
}
```

| HTTP | Code |
| --- | --- |
| 400 | `invalid_track_id` |
| 400 | `invalid_stream_id` |
| 400 | `anonymous_session_required` |
| 400 | `invalid_anonymous_session` |
| 400 | `invalid_idempotency_key` |
| 400 | `invalid_active_playback_seconds` |
| 400 | `invalid_paused_seconds` |
| 400 | `invalid_seek_count` |
| 400 | `invalid_ending_position_seconds` |
| 404 | `track_not_found` |
| 404 | `playback_session_not_found` (also used for wrong ownership) |
| 409 | `idempotency_key_reused` |

## The Scene

### `GET /api/charts/weekly`

Public. Returns the exact current Monday 00:00 UTC through next Monday 00:00 UTC
half-open window. The service ranks the top 50 eligible tracks and may include
eligible tracks with zero plays.

Formula and deterministic order:

1. `rankingScore = weeklyQualifiedPlays`, descending;
2. weekly qualified plays, descending;
3. track `CreatedAt`, descending (newer publication first);
4. track UUID, ascending.

Response:

```json
{
  "success": true,
  "data": {
    "weekOf": "2026-07-13T00:00:00.0000000Z",
    "basis": "weekly_plays",
    "chartWindowStart": "2026-07-13T00:00:00Z",
    "chartWindowEnd": "2026-07-20T00:00:00Z",
    "generatedAt": "2026-07-13T12:00:30Z",
    "dataThrough": "2026-07-13T12:00:30Z",
    "isStale": false,
    "computedAt": "2026-07-13T12:00:30.0000000Z",
    "entries": [
      {
        "rank": 1,
        "trackId": "00000000-0000-0000-0000-000000000000",
        "weeklyQualifiedPlays": 5,
        "lifetimePlays": 42,
        "rankingScore": 5,
        "title": "Track",
        "artist": "Artist",
        "creatorId": "creator-id",
        "coverArtUrl": null,
        "deltaRank": null
      }
    ],
    "trackOfTheWeek": null
  }
}
```

Missing, older-than-SLA, or behind-ledger snapshots are recomputed on read. A worker
runs every 30 seconds by default. `Charts:Weekly:StaleAfterSeconds` defaults to 60;
`Charts:Weekly:WorkerIntervalSeconds` defaults to 30. A failed refresh preserves the
last current-week snapshot and returns `isStale=true`. The previous week is never
substituted for a missing current week.

Lifetime trending is intentionally a separate chart type. `/trending`,
`/tracks/trending`, `/activity/trending`, and `/api/public/trending` all order eligible
tracks by lifetime `TrackStats.PlayCount` descending, then `CreatedAt` descending,
then track UUID ascending. `Track.TrendingScore` is not an active ranking input.

## Maintenance and health

Admin role required:

- `POST /admin/play-reconciliation/dry-run` compares ledger and projections without
  changing counts. Body: `{ "trackIds": null, "mismatchLimit": 100 }`.
- `POST /admin/play-reconciliation/repair` repairs bounded projections without
  creating events. Body: `{ "trackIds": null, "trackBatchSize": 25,
  "eventBatchSize": 1000 }`.
- `POST /admin/charts/aggregate` rebuilds the current chart window.
- `GET /health/details` includes database state, pending aggregation age/lag, and
  chart freshness. Public `GET /health` remains liveness-only.

Repair uses `PlayCount = LegacyPlayCount + ledger count`, marks pending events in the
same transaction, writes an audit record, and uses PostgreSQL advisory/row locks so a
concurrent accepted play is not overwritten.

## Deployment and rollback

1. Back up the database and run the migration before enabling new application
   instances. Drain old instances so none can create raw legacy sessions after the
   baseline snapshot.
2. Apply `20260713203628_QualifiedPlayLedgerAndChartFreshness`.
3. Deploy all backend instances, then call the admin dry-run endpoint.
4. Inspect mismatch, duplicate, legacy-history, pending-lag, nonzero legacy
   `TrendingScore`, and chart-age fields. Run bounded repair only after reviewing the
   dry-run.
5. Rebuild the current chart and verify `isStale=false` and the expected watermark.

The down migration preserves the combined `TrackStats.PlayCount` column but drops
the qualified-event evidence and new split columns. Take a database backup before
rollback; prefer rolling forward. If rollback is unavoidable, stop new play traffic,
export `QualifiedPlayEvents`, apply down migration, and redeploy the prior binary.
