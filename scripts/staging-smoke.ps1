# staging-smoke.ps1 — Hard deployment gate.
#
# Runs against a live deployment (staging or production) after the container
# starts serving. Exits non-zero on ANY failure so CI can block the release.
#
# What it checks (in order):
#   1. /qa-preflight returns 200 with status=ok and every dep ok/skip.
#      This is the single load-bearing check — /health always returns 200 by
#      design (so Render keeps routing traffic), so /qa-preflight is the only
#      way to catch a boot-but-broken deploy.
#   2. /catalog returns a non-empty track list with the expected JSON shape.
#   3. /api/v1/tracks (public v1 API) returns a valid paginated response.
#
# Usage:
#   pwsh scripts/staging-smoke.ps1 -BaseUrl https://staging.api.cambrianmusic.com
#   pwsh scripts/staging-smoke.ps1 -BaseUrl https://api.cambrianmusic.com
#
# Exit codes:
#   0  all checks passed
#   1  any check failed

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [int]$TimeoutSec = 20
)

$ErrorActionPreference = 'Stop'
$failures = @()

function Invoke-SmokeCheck {
    param([string]$Name, [scriptblock]$Check)
    Write-Host "→ $Name" -NoNewline
    try {
        & $Check
        Write-Host " ✓"
    }
    catch {
        Write-Host " ✗"
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
        $script:failures += $Name
    }
}

$BaseUrl = $BaseUrl.TrimEnd('/')
Write-Host "Smoke testing $BaseUrl"
Write-Host ""

# ── 1. /qa-preflight — every dependency must report ok or skip ──
Invoke-SmokeCheck 'qa-preflight reports all dependencies healthy' {
    $res = Invoke-WebRequest "$BaseUrl/qa-preflight" `
        -UseBasicParsing -TimeoutSec $TimeoutSec -SkipHttpErrorCheck
    if ($res.StatusCode -ne 200) {
        throw "expected 200, got $($res.StatusCode). Body: $($res.Content)"
    }
    $body = $res.Content | ConvertFrom-Json
    if ($body.status -ne 'ok') {
        throw "overall status=$($body.status), expected ok"
    }
    foreach ($dep in 'db', 'storage', 'stripe') {
        $s = $body.$dep.status
        if ($s -ne 'ok' -and $s -ne 'skip') {
            throw "dep '$dep' reported status=$s, error=$($body.$dep.error)"
        }
    }
}

# ── 2. /catalog — marketplace must return at least one track ──
Invoke-SmokeCheck 'GET /catalog returns tracks' {
    $res = Invoke-RestMethod "$BaseUrl/catalog" -TimeoutSec $TimeoutSec
    # The catalog controller envelopes results; tolerate either shape.
    $tracks = if ($res.data) { $res.data } elseif ($res.tracks) { $res.tracks } else { $res }
    if ($null -eq $tracks -or @($tracks).Count -lt 1) {
        throw "catalog returned 0 tracks — either seeding failed or the query broke"
    }
    $first = @($tracks)[0]
    foreach ($field in 'id', 'title') {
        if (-not $first.PSObject.Properties.Name.Contains($field)) {
            throw "track missing required field '$field'. Got: $(($first | ConvertTo-Json -Depth 2))"
        }
    }
}

# ── 3. /api/v1/tracks — public developer API must be reachable ──
Invoke-SmokeCheck 'GET /api/v1/tracks returns paginated list' {
    $res = Invoke-WebRequest "$BaseUrl/api/v1/tracks?limit=1" `
        -UseBasicParsing -TimeoutSec $TimeoutSec -SkipHttpErrorCheck
    # 200 (normal) or 429 (under rate limit); anything else is a failure.
    if ($res.StatusCode -ne 200) {
        throw "expected 200, got $($res.StatusCode). Body: $($res.Content)"
    }
    $body = $res.Content | ConvertFrom-Json
    if ($null -eq $body) {
        throw "empty response body"
    }
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "SMOKE FAILED — $($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  • $_" -ForegroundColor Red }
    exit 1
}

Write-Host "All smoke checks passed." -ForegroundColor Green
exit 0
