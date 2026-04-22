# Pre-Deploy Checklist

Every item below is proven by an automated test. If a row has no test, it does
not ship — write the test first.

This is the customer-visible surface. If any of these break, users will notice
within minutes.

| # | Customer-visible behavior | Automated test that proves it |
|---|---------------------------|-------------------------------|
| 1 | Creator can upload a track via `POST /upload` | `tests/Cambrian.Api.Tests/FullPurchaseFlowE2ETests.cs` (step 1), `UploadValidationTests.cs`, `UploadServiceTests.cs` |
| 2 | `GET /catalog` returns at least one public track with the fields the frontend reads | `tests/Cambrian.Api.Tests/CatalogControllerTests.cs`, `CatalogServiceTests.cs`, `StorefrontTests.cs` |
| 3 | Creator can edit a track's per-tier prices via `PUT /creator/tracks/{id}` | `tests/Cambrian.Api.Tests/CreatorControllerTests.cs`, `FullPurchaseFlowE2ETests.cs` (step 2) |
| 4 | Prices are stored and returned as integer cents — never fractional, never dollars | `tests/Cambrian.Api.Tests/CatalogServiceTests.cs`, `TrackRequestValidationTests.cs`, `InvariantTests.cs` |
| 5 | Buyer can start checkout via `POST /checkout` and receives a Stripe URL | `tests/Cambrian.Api.Tests/CheckoutControllerTests.cs`, `CheckoutServiceTests.cs`, `PurchaseJourneyTests.cs` |
| 6 | `POST /webhook/stripe` on `checkout.session.completed` creates exactly one `Purchase` and one `LibraryItem` | `tests/Cambrian.Api.Tests/WebhookEndToEndTests.cs`, `StripeWebhookServiceTests.cs`, `FullPurchaseFlowE2ETests.cs` (step 3) |
| 7 | Duplicate Stripe webhook deliveries (same EventId) never double-credit | `tests/Cambrian.Api.Tests/ConcurrencyTests.cs` (parallel fanout=8), `StripeWebhookServiceTests.cs` |
| 8 | `GET /library` returns the purchased track for the buyer | `tests/Cambrian.Api.Tests/LibraryControllerTests.cs`, `LibraryTests.cs`, `FullPurchaseFlowE2ETests.cs` (step 4) |
| 9 | `GET /stream/{trackId}/audio` redirects or streams audio only for entitled users | `tests/Cambrian.Api.Tests/StreamControllerTests.cs`, `FullPurchaseFlowE2ETests.cs` (step 5) |
| 10 | Exclusive license race: two simultaneous purchases → only one wins | `tests/Cambrian.Api.Tests/StripeWebhookServiceTests.cs`, `CopyrightBuyoutTests.cs` |
| 11 | Creator wallet credited exactly `floor(gross × (1 − feeRate))` on purchase | `tests/Cambrian.Api.Tests/PayoutServiceTests.cs`, `StripeWebhookServiceTests.cs`, `BillingTierTests.cs` |
| 12 | Payout request is atomic — balance check + debit + Payout row all commit together or none do | `tests/Cambrian.Api.Tests/PayoutServiceTests.cs` |
| 13 | License certificate issued per purchase and verifiable at `/api/v1/licenses/{id}/verify` | `tests/Cambrian.Api.Tests/LicenseCertificateIntegrationTests.cs`, `V1/*` |
| 14 | JWT rejects tampered/expired tokens; `/auth/login` issues valid ones | `tests/Cambrian.Api.Tests/AuthControllerTests.cs`, `AuthIntegrationTests.cs`, `Phase1SecurityTests.cs` |
| 15 | API key auth: `X-API-Key` accepted, revoked keys 401, raw key never returned after creation | `tests/Cambrian.Api.Tests/V1/*` |
| 16 | Rate limiting: global + auth limits enforced, bypassed in Testing env | `tests/Cambrian.Api.Tests/Security/*`, `AuthTests.cs` |
| 17 | All EF migrations apply cleanly against a fresh Postgres 16 | `tests/Cambrian.Api.Tests/MigrationApplyTests.cs` (runs in CI `integration-tests` job with Postgres service) |
| 18 | OpenAPI contract still matches the compiled assembly | `.github/workflows/ci.yml` → `contract-validation` job (Swashbuckle CLI + `scripts/validate-contracts.cjs` + drift diff) |
| 19 | No NuGet packages with known vulnerabilities | `.github/workflows/ci.yml` → `security` job |
| 20 | After deploy, DB + Storage + Stripe all respond correctly against the live deployment | `scripts/staging-smoke.ps1` → `/qa-preflight` (returns 503 on any failure) |

## How the gate works

- **Pre-merge:** `.github/workflows/ci.yml` runs `unit-tests`, `integration-tests`
  (including `Category=Critical|Integration|Concurrency|Postgres`),
  `contract-validation`, and `security`. The `gate` job fails if any is not
  `success`, which blocks the PR.
- **Post-deploy (staging):** `deploy-staging.yml` waits for `/health`, then
  runs `staging-smoke.ps1` against the live URL. `/qa-preflight` returning
  anything other than 200 fails the workflow.
- **Post-deploy (production):** same as staging, but the deploy job is gated
  behind the `production` GitHub Environment, which requires a manual approval
  step before the container starts.

## What to do if a check isn't listed here

If you're adding a new customer-visible behavior, add the row before merging.
If you're adding a new test, add the row at the same time — this file is the
map from "the product works" to "the test suite proves it."
