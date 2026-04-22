# Five-Minute Manual QA Walkthrough

Run this after any deploy that touches the purchase path, the creator tools, or
the upload/storage pipeline. It takes ~5 minutes against staging and exercises
the full money flow end-to-end — the parts an automated test cannot verify
(real Stripe redirect, real audio byte range from Supabase, real email).

Everything below is a single customer journey. If any step diverges from
expected behavior, **stop and file a regression test before shipping** — see
`qa/REGRESSION_POLICY.md`.

## Prereqs

- Staging URL of the web app (frontend), not the API.
- Stripe test card `4242 4242 4242 4242`, any future expiry, any CVC.
- A throwaway email you can read (+ a different email for the creator account).

## The walk

### 1 min — Register as a creator

1. Sign up as `creator+<date>@yourdomain`.
2. Set a username (triggers role promotion to `Creator`).
3. Verify your email (click the link in the console email in dev, or the real
   email in staging).

**Expected:** you land on the creator dashboard. Role shown as `Creator`.

### 1 min — Upload and price a track

1. Upload a small MP3 (any file under 50 MB).
2. Wait for the upload to complete — the track card should show a cover
   placeholder and a duration.
3. Edit the track: set `nonExclusivePriceCents` to `2499`, `exclusivePriceCents`
   to `19999`. Save.

**Expected:** prices display as `$24.99` and `$199.99`. No rounding to dollars,
no display of floats. Cover art placeholder is visible (not broken image).

### 1 min — Register as a buyer and purchase

1. In an incognito window, register as `buyer+<date>@yourdomain`.
2. Open the track you just uploaded (use the public `/catalog` search).
3. Click "Buy non-exclusive license" → redirected to Stripe Checkout.
4. Pay with `4242 4242 4242 4242`, future expiry, any CVC.

**Expected:** redirected back to the app with a success state. A library entry
appears within ~5 seconds (webhook delivery).

### 1 min — Verify the buyer's entitlement

1. Go to `/library`. The purchased track must be there.
2. Press play. Audio must start within 2 seconds.
3. Scrub forward 30 seconds. Audio must continue (Range request working).
4. Download the license PDF.

**Expected:** audio plays, scrubbing works on Safari (tests iOS Range header
path), license PDF has buyer name, track title, and license type.

### 1 min — Verify the creator side

1. Switch back to the creator window.
2. Go to the creator dashboard → wallet/earnings.
3. Confirm the balance increased by `floor(2499 × (1 − feeRate))` cents for
   the non-exclusive sale. For a Free-tier creator, check
   `TierManifest.For(CreatorTier.Free).FeeRate`.
4. Check the activity feed — "Track sold: ..." should be present.

**Expected:** wallet balance matches the math exactly. No rounding up.

## Red flags (stop-ship)

- Price displayed as `$24` or `$24.990` — integer-cents invariant broken.
- Library entry missing >30s after Stripe redirect — webhook not processing.
- Play button does nothing on iOS Safari — Range header path regressed.
- Wallet balance is wrong by more than 1 cent — fee computation changed.
- Two charges for one purchase — idempotency check broken (STOP, file incident).

## Out of scope for this walkthrough

The following are covered by the automated suite, not manual QA:
- Exclusive sale race conditions (`ConcurrencyTests.cs`).
- Duplicate webhook deliveries (`ConcurrencyTests.cs`).
- Migrations (`MigrationApplyTests.cs`).
- Rate limiting (`Security/*`).
- OpenAPI contract drift (`contract-validation` CI job).
