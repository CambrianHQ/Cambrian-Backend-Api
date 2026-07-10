# Staging Contract Sync Follow-Up

## Goal

After the creator profile backend fix is deployed to staging, regenerate the
frontend OpenAPI client from the staging backend and verify the DAW type change
is reflected without unrelated client churn.

## Status

Blocked until the backend branch containing the creator profile DAW contract fix
is deployed to staging.

## Scope

- Backend contract change: `studioSetup.daw` is now a DAW tag list, emitted as an
  array of strings, matching the other `studioSetup` chip fields.
- Backward compatibility: legacy payloads with a single DAW string are accepted
  by the backend, but the normalized response and OpenAPI client should use the
  array shape.
- Frontend work: generated OpenAPI client sync only, followed by focused creator
  profile verification.

## Checklist

- [ ] Deploy backend branch to staging.
- [ ] Confirm staging exposes the updated creator profile contract.
- [ ] Run `npm run generate:api` in the frontend repo against staging.
- [ ] Review the generated diff carefully.
- [ ] Confirm only intended creator profile / DAW contract changes are included.
- [ ] Run frontend typecheck, lint, and targeted creator profile tests.
- [ ] Commit the generated client update separately.
- [ ] Document that this resolves the DAW contract sync follow-up.

## Verification Notes

Confirm the staging OpenAPI document exposes `StudioSetupDto.daw` as an array of
strings. The runtime compatibility path for legacy single-string DAW values is a
backend implementation detail and should not make the generated frontend client
model widen to arbitrary unrelated shapes.

Recommended frontend validation:

```powershell
npm run generate:api
git diff --stat
git diff
npm run typecheck
npm run lint
```

Run the frontend repo's existing targeted creator profile test command after
generation. If the generated diff includes unrelated endpoint, schema, or client
runtime churn, stop and reconcile staging contract drift before committing.

Suggested standalone commit message:

```text
chore(api): sync creator profile daw contract
```
