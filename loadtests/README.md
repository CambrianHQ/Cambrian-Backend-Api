# Cambrian Load Tests (k6)

## Prerequisites

Install [k6](https://k6.io/docs/get-started/installation/):

```bash
# macOS
brew install k6

# Windows
choco install k6

# Linux (Debian/Ubuntu)
sudo gpg -k
sudo gpg --no-default-keyring --keyring /usr/share/keyrings/k6-archive-keyring.gpg --keyserver hkp://keyserver.ubuntu.com:80 --recv-keys C5AD17C747E3415A3642D57D77C6C491D6AC1D68
echo "deb [signed-by=/usr/share/keyrings/k6-archive-keyring.gpg] https://dl.k6.io/deb stable main" | sudo tee /etc/apt/sources.list.d/k6.list
sudo apt-get update && sudo apt-get install k6
```

## Test Scenarios

| Script | Target | Auth | Description |
|--------|--------|------|-------------|
| `smoke.js` | All critical endpoints | None | Quick post-deploy health check (1 VU, 30s) |
| `catalog.js` | `/catalog`, `/discover`, `/trending` | None | Public discovery under load (ramps to 200 VUs) |
| `authenticated.js` | `/library`, `/auth/me`, `/billing/status` | JWT | Authenticated user flows (ramps to 100 VUs) |
| `streaming.js` | `/stream/{id}/audio` | None | Audio redirect latency (ramps to 150 VUs) |
| `resilience.js` | `/catalog`, `/discover`, `/health` | None | Chaos-tolerant smoke test for outage and degradation drills |

## Running

### Smoke test (post-deploy gate)
```bash
k6 run loadtests/smoke.js
```

### Catalog discovery load
```bash
k6 run loadtests/catalog.js
```

### Against staging
```bash
k6 run -e BASE_URL=https://cambrian-api-staging-99kn.onrender.com loadtests/catalog.js
```

### Authenticated flows (requires JWT)
```bash
# Login to get a token first, then:
k6 run -e BASE_URL=https://cambrian-api-staging-99kn.onrender.com -e AUTH_TOKEN=<jwt> loadtests/authenticated.js
```

### Streaming performance
```bash
k6 run -e BASE_URL=https://cambrian-api-staging-99kn.onrender.com loadtests/streaming.js
```

### Resilience drill
```bash
k6 run -e BASE_URL=https://cambrian-api-staging-99kn.onrender.com loadtests/resilience.js
```

## Performance Benchmarks

| Endpoint | Target p95 | Notes |
|----------|-----------|-------|
| `/health` | < 100ms | Simple DB check |
| `/catalog` | < 300ms | Paginated, may be cached |
| `/discover` | < 300ms | Filtered query |
| `/tracks/{id}` | < 300ms | Single record lookup |
| `/trending` | < 300ms | Cached aggregation |
| `/stream/{id}/audio` | < 500ms | Presigned URL generation |
| `/library` | < 300ms | Indexed by userId |
| `/checkout` | < 500ms | Stripe session creation |
| `/auth/me` | < 200ms | JWT decode + DB lookup |

## CI/CD Integration

Add to GitHub Actions workflow:

```yaml
- name: Smoke test
  run: k6 run loadtests/smoke.js
  env:
    BASE_URL: ${{ secrets.STAGING_API_URL }}
```

For manual chaos or resilience runs, see `.github/workflows/resilience-tests.yml`.
