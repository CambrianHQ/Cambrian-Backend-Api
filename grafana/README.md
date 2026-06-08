# Grafana Dashboards

Dashboard-as-code JSON files for Cambrian operational observability.

**Fastest path:** run the local stack in [`../observability`](../observability) — it
auto-provisions these dashboards and a matching Prometheus datasource. See
`../observability/README.md`.

**Manual import:** **Dashboards → Import → Upload JSON**. Panels reference a Prometheus
datasource by uid `cambrian-prometheus`; if your datasource has a different uid, pick it in
the import dialog (or name your Prometheus datasource uid `cambrian-prometheus`).

> Metric/label names follow OpenTelemetry semantic conventions
> (`http_route`, `http_request_method`, `http_response_status_code`, `server_address`) as
> emitted by the API's `/metrics` endpoint.

## Dashboards

| File | Purpose |
|------|---------|
| `executive-system-health.json` | Top-level availability, latency, error rate, resource usage |
| `api-performance-errors.json` | Endpoint-centric latency heatmap, throughput, 4xx/5xx by route |
| `checkout-webhook-revenue.json` | Purchase funnel, webhook processing, Stripe latency, payouts |
| `chaos-experiment-observability.json` | Active experiment tracking, dependency errors, recovery time |

## Prerequisites

These dashboards expect:

1. **OpenTelemetry** instrumented in the .NET backend (ASP.NET Core metrics + HTTP client metrics)
2. **Prometheus** scraping the `/metrics` endpoint (or OTLP exporter → Prometheus-compatible store)
3. **Grafana** with a Prometheus data source named `Prometheus`

### Custom Application Metrics

The checkout/webhook dashboard uses custom counters that must be emitted from application code:

| Metric | Type | Description |
|--------|------|-------------|
| `cambrian_checkout_started_total` | Counter | Checkout session initiated |
| `cambrian_checkout_completed_total` | Counter | Checkout completed successfully |
| `cambrian_checkout_failed_total` | Counter | Checkout failed |
| `cambrian_webhook_processed_total` | Counter | Webhook events processed |
| `cambrian_webhook_duplicate_total` | Counter | Duplicate webhook events skipped |
| `cambrian_webhook_failed_total` | Counter | Webhook processing failures |
| `cambrian_library_grant_total` | Counter | Tracks added to buyer library |
| `cambrian_upload_completed_total` | Counter | Track uploads completed |
| `cambrian_upload_failed_total` | Counter | Track uploads failed |
| `cambrian_stream_signed_url_issued_total` | Counter | Signed audio URLs issued |
| `cambrian_payout_created_total` | Counter | Payouts requested |
| `cambrian_payout_approved_total` | Counter | Payouts approved |

### Chaos Experiment Metrics

The chaos dashboard uses optional gauges/infos:

| Metric | Type | Description |
|--------|------|-------------|
| `cambrian_chaos_experiment_active` | Gauge | 1 if experiment running, label `experiment` |
| `cambrian_chaos_phase_info` | Info | Label `phase` = `before`/`during`/`after` |
| `cambrian_chaos_recovery_seconds` | Gauge | Seconds from experiment end to 99% success rate |

## Recommended Stack

```
ASP.NET Core  →  OpenTelemetry SDK  →  OTLP Exporter
                                            │
                                            ▼
                                       Prometheus  →  Grafana
                                            │
                                       Loki (logs)
                                       Tempo (traces)
```
