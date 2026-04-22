# Regression Policy — Every Bug Becomes a Test

**This rule is inviolable. No exceptions, ever.**

A bug fix without a test is a bug waiting to come back.

## The rule

**No bug fix merges without a test that fails before the fix and passes after.**

- No "just this once."
- No "the fix is too small to need a test."
- No "I'll add a test in a follow-up PR."
- No "it's too hard to reproduce" — that is a reason to dig deeper, not a
  reason to skip the test.
- No "it only happens in prod" — a flaky reproducer is still more valuable
  than no reproducer.

The test proves two things:
1. You actually reproduced the bug (the test failed on `main` at HEAD).
2. Your fix actually addresses it (the test passes after your change).

If you cannot write a failing test first, you do not understand the bug well
enough to fix it. Either dig deeper, or pair with someone who can.

This rule applies to human contributors and AI assistants equally. An AI
assistant fixing a bug in this repo must write the regression test in the
same turn as the fix — never as a follow-up, never as an optional extra.

## Where to put the test

| Bug type | File to add the test to |
|----------|-------------------------|
| Controller/HTTP-contract bug | `tests/Cambrian.Api.Tests/<Controller>ControllerTests.cs` |
| Service/business-logic bug | `tests/Cambrian.Api.Tests/<Service>ServiceTests.cs` |
| Webhook / payment / payout bug | `tests/Cambrian.Api.Tests/StripeWebhookServiceTests.cs`, `PayoutServiceTests.cs`, or `PurchaseJourneyTests.cs` |
| Race condition | `tests/Cambrian.Api.Tests/ConcurrencyTests.cs` — add a `Parallel.ForEachAsync`-style test that reliably reproduces |
| Migration / schema bug | A new integration test that applies migrations and queries the affected shape (see `MigrationApplyTests.cs`) |
| Customer-journey regression (register → buy → library → play) | `tests/Cambrian.Api.Tests/FullPurchaseFlowE2ETests.cs` or a new E2E file with `[Trait("Category", "Critical")]` |
| Contract drift (OpenAPI mismatch) | The fix is in `contracts/openapi.v1.json` and the CI `contract-validation` job catches future drift automatically |

## Trait conventions

Tag the test correctly so CI runs it in the right lane:

- `[Trait("Category", "Critical")]` — customer journey, blocks deploy.
- `[Trait("Category", "Integration")]` — multi-component, uses fixture.
- `[Trait("Category", "Concurrency")]` — parallel execution.
- `[Trait("Category", "Postgres")]` — requires real Postgres (runs in CI
  `integration-tests` job, skipped on local runs without Docker).
- No trait — fast unit test, runs in both `unit-tests` and `integration-tests`
  CI jobs.

## Naming

Name the test after what was broken, not what the fix does.

- ✅ `Webhook_Does_Not_Double_Credit_On_Duplicate_Delivery`
- ✅ `Catalog_Search_Matches_On_Mood_Not_Just_Title`
- ❌ `Fix_For_Bug_123`
- ❌ `Test_StripeWebhookService_Works`

The name is documentation. Someone reading the test list should be able to see
the shape of the bug from the title alone.

## Protected-system bugs

Bugs in any system listed in `CLAUDE.md` §12 (payout flow, Stripe Connect
config, webhook signature, exclusive/copyright atomic SQL, applied migrations,
JWT validation, TierManifest, API key hashing) follow the same rule *and* a
human must review the test before merge. The test for a payout bug is itself
a protected artifact — do not weaken or delete it in later refactors.

## When the test must be deleted

Only when the behavior it locks in is intentionally removed. If that happens,
the delete must reference the decision (PR number / issue) in the commit
message, so the removal is auditable. "Flaky" is not a reason to delete a
regression test — it's a reason to fix it. A race-condition regression
reproducer that is sometimes green is still serving its purpose; if it fails
1% of the time, that's signal.

## Summary

1. Reproduce the bug with a test that fails on `main`.
2. Fix the bug. Watch the test go green.
3. Commit both in the same PR.
4. If the test file doesn't exist yet, create it under the table above.
5. Tag with the right `[Trait]` so CI routes it correctly.

That's the whole policy. Cost ~5 minutes per fix, saves weeks of incident
response over the life of the project.
