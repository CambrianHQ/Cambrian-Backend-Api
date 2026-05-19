#!/usr/bin/env bash
# scripts/migrate-render-to-supabase.sh
#
# Snapshots the production Render Postgres and restores it to Supabase.
# Used in MIGRATION.md steps 2-3.
#
# Usage:
#   export RENDER_DB_URL="postgresql://cambrian:...@dpg-xxx.oregon-postgres.render.com/cambrian_prod"
#   export SUPABASE_DB_URL="postgresql://postgres:...@db.xxx.supabase.co:5432/postgres"
#   ./scripts/migrate-render-to-supabase.sh         # dump + restore + verify
#   ./scripts/migrate-render-to-supabase.sh dump    # dump only
#   ./scripts/migrate-render-to-supabase.sh restore <file>   # restore an existing dump
#
# Requires: pg_dump, pg_restore, psql (Postgres 16 client tools).

set -euo pipefail

DUMP_DIR="${DUMP_DIR:-./db-dumps}"
mkdir -p "$DUMP_DIR"

require_env() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "ERROR: \$$name is not set." >&2
    exit 1
  fi
}

require_tool() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "ERROR: '$1' not found in PATH." >&2
    echo "Install Postgres 16 client tools: brew install postgresql@16" >&2
    exit 1
  fi
}

check_pg_version() {
  local v
  v=$(pg_dump --version | grep -oE '[0-9]+' | head -1)
  if [[ "$v" -lt 16 ]]; then
    echo "WARNING: pg_dump is version $v, but Render runs Postgres 16." >&2
    echo "Dumps may fail on newer features. Consider upgrading: brew install postgresql@16" >&2
  fi
}

do_dump() {
  require_env RENDER_DB_URL
  require_tool pg_dump
  check_pg_version

  local stamp
  stamp=$(date +%Y%m%d-%H%M%S)
  local file="$DUMP_DIR/cambrian-prod-$stamp.dump"

  echo ">> Dumping Render Postgres to $file"
  pg_dump "$RENDER_DB_URL" \
    --no-owner \
    --no-privileges \
    --no-publications \
    --no-subscriptions \
    --format=custom \
    --file="$file"

  local size
  size=$(du -h "$file" | cut -f1)
  echo ">> Dump complete: $file ($size)"
  echo "$file"
}

do_restore() {
  local file="$1"
  require_env SUPABASE_DB_URL
  require_tool pg_restore

  if [[ ! -f "$file" ]]; then
    echo "ERROR: dump file not found: $file" >&2
    exit 1
  fi

  echo ">> Restoring $file to Supabase"
  echo ">> This will DROP existing objects with --clean --if-exists."
  read -r -p ">> Continue? [y/N] " ans
  if [[ "$ans" != "y" && "$ans" != "Y" ]]; then
    echo "Aborted."
    exit 1
  fi

  pg_restore \
    --dbname="$SUPABASE_DB_URL" \
    --no-owner \
    --no-privileges \
    --clean \
    --if-exists \
    "$file"

  echo ">> Restore complete."
}

do_verify() {
  require_env RENDER_DB_URL
  require_env SUPABASE_DB_URL
  require_tool psql

  local script_dir
  script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  exec "$script_dir/verify-db-migration.sh"
}

case "${1:-all}" in
  dump)
    do_dump
    ;;
  restore)
    if [[ -z "${2:-}" ]]; then
      echo "Usage: $0 restore <dump-file>" >&2
      exit 1
    fi
    do_restore "$2"
    ;;
  verify)
    do_verify
    ;;
  all)
    file=$(do_dump | tail -1)
    do_restore "$file"
    do_verify
    ;;
  *)
    echo "Usage: $0 [dump|restore <file>|verify|all]" >&2
    exit 1
    ;;
esac
