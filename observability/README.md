# Local Observability Stack

Spins up **Prometheus** + **Grafana** locally and auto-loads the dashboards in
[`../grafana/dashboards`](../grafana/dashboards). Prometheus scrapes the API's
`/metrics` endpoint (added via OpenTelemetry — see `src/Cambrian.Api/Program.cs`).

## 1. Run the API so containers can scrape it

The API must listen on **all interfaces** (not just loopback) so the Prometheus
container can reach it via `host.docker.internal`:

```powershell
# Postgres for the API (from repo root)
docker compose up -d db

# Run the API bound to 0.0.0.0:5000
$env:ASPNETCORE_URLS = "http://+:5000"
dotnet run --project src/Cambrian.Api
```

Confirm metrics are exposed:

```powershell
curl http://localhost:5000/metrics
```

## 2. Start Prometheus + Grafana

```powershell
docker compose -f observability/docker-compose.yml up -d
```

- Prometheus → http://localhost:9090 (check **Status → Targets**: `cambrian-api` should be `UP`)
- Grafana → http://localhost:3000 (anonymous admin; dashboards under the **Cambrian** folder)

## 3. Generate traffic

```powershell
k6 run loadtests/smoke.js          # defaults to http://localhost:5000
```

Within ~30s the **API Performance & Errors** and **Checkout / Webhook / Revenue**
dashboards populate. Custom `cambrian_*` business counters only appear once their
event has fired at least once (e.g. an upload, a payout, a webhook).

## Scraping a different target

Edit `observability/prometheus/prometheus.yml` and change the `targets` entry
(e.g. to a staging URL), then `docker compose -f observability/docker-compose.yml restart prometheus`.

## Notes

- Grafana anonymous-admin + no-login is **local convenience only**. Never expose this config publicly.
- `/metrics` is unauthenticated and outside the OpenAPI contract; in production it must be
  network-restricted rather than exposed to the internet.
- The DB query-duration panel (`db_client_*`) is best-effort and may be empty unless EF/Npgsql
  metrics are emitting those series.
