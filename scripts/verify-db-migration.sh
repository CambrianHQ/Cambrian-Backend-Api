#!/usr/bin/env bash
# scripts/verify-db-migration.sh
#
# Compares row counts and migration history between Render (source) and
# Supabase (destination) Postgres databases. Run after restore.
#
# Usage:
#   export RENDER_DB_URL="postgresql://..."
#   export SUPABASE_DB_URL="postgresql://..."
#   ./scripts/verify-db-migration.sh
#
# Exits non-zero if any table has mismatched row counts or if the latest
# EF migration doesn't match.

set -euo pipefail

if [[ -z "${RENDER_DB_URL:-}" ]]; then
  echo "ERROR: \$RENDER_DB_URL is not set." >&2
  exit 1
fi
if [[ -z "${SUPABASE_DB_URL:-}" ]]; then
  echo "ERROR: \$SUPABASE_DB_URL is not set." >&2
  exit 1
fi
if ! command -v psql >/dev/null 2>&1; then
  echo "ERROR: psql not found in PATH." >&2
  exit 1
fi

# Tables to verify — covers everything with significant data per CLAUDE.md §4.
TABLES=(
  "AspNetUsers"
  "AspNetRoles"
  "AspNetUserRoles"
  "Tracks"
  "Creators"
  "CreatorFollows"
  "CreatorProfiles"
  "Purchases"
  "LibraryItems"
  "LicenseCertificates"
  "Payouts"
  "WalletTransactions"
  "Invoices"
  "StripeWebhookEvents"
  "Subscriptions"
  "AnalyticsEvents"
  "FeatureFlags"
  "TrackCollections"
  "ActivityItems"
  "ApiKeys"
)

count_rows() {
  local db_url="$1"
  local table="$2"
  psql "$db_url" -tAc "SELECT COUNT(*) FROM \"$table\";" 2>/dev/null || echo "ERR"
}

latest_migration() {
  local db_url="$1"
  psql "$db_url" -tAc 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;' 2>/dev/null || echo "ERR"
}

printf "\n%-30s %12s %12s %10s\n" "TABLE" "RENDER" "SUPABASE" "STATUS"
printf -- "-%.0s" {1..70}; echo

fail=0
for t in "${TABLES[@]}"; do
  r=$(count_rows "$RENDER_DB_URL" "$t")
  s=$(count_rows "$SUPABASE_DB_URL" "$t")
  if [[ "$r" == "ERR" || "$s" == "ERR" ]]; then
    status="ERROR"
    fail=1
  elif [[ "$r" == "$s" ]]; then
    status="OK"
  else
    status="MISMATCH"
    fail=1
  fi
  printf "%-30s %12s %12s %10s\n" "$t" "$r" "$s" "$status"
done

echo
echo "Latest EF migration:"
r_mig=$(latest_migration "$RENDER_DB_URL")
s_mig=$(latest_migration "$SUPABASE_DB_URL")
echo "  Render:   $r_mig"
echo "  Supabase: $s_mig"
if [[ "$r_mig" != "$s_mig" ]]; then
  echo "  STATUS:   MISMATCH — app may attempt to re-run migrations on first boot." >&2
  fail=1
else
  echo "  STATUS:   OK"
fi

echo
if [[ $fail -eq 0 ]]; then
  echo "All checks passed."
  exit 0
else
  echo "One or more checks failed. Do not cut over DNS until resolved." >&2
  exit 1
fi
