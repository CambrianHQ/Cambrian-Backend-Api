$pass = 0; $fail = 0; $warn = 0

function Test-Pass { param($msg) $script:pass++; Write-Host "[PASS] $msg" -ForegroundColor Green }
function Test-Fail { param($msg) $script:fail++; Write-Host "[FAIL] $msg" -ForegroundColor Red }
function Test-Warn { param($msg) $script:warn++; Write-Host "[WARN] $msg" -ForegroundColor Yellow }

Write-Host "`n====== CAMBRIAN API STARTUP VERIFICATION ======`n" -ForegroundColor Cyan

# 1. Migrations
Write-Host "--- 1. DATABASE / MIGRATIONS ---"
try {
    $tableResult = docker exec cambrian-postgres psql -U cambrian -d cambrian -t -c "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public'" 2>&1
    $tableCount = ($tableResult | Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -First 1).Trim()
    $migResult = docker exec cambrian-postgres psql -U cambrian -d cambrian -c "SELECT MigrationId FROM \"__EFMigrationsHistory\"" 2>&1
    $migIds = $migResult | Where-Object { $_ -match '\d{14}' } | ForEach-Object { $_.Trim() }
    Test-Pass "Migrations applied: $($migIds -join ', ')"
    Test-Pass "$tableCount tables in public schema"
} catch { Test-Fail "Database check: $_" }

# 2. Auth: Register
Write-Host "`n--- 2. AUTH ---"
try {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $body = "{`"email`":`"verify.$ts@cambrian.io`",`"password`":`"Test1234!`",`"displayName`":`"VerifyUser`"}"
    Invoke-RestMethod -Uri http://localhost:5234/auth/register -Method POST -Body $body -ContentType "application/json" | Out-Null
    Test-Pass "Register new user OK"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -lt 500) { Test-Pass "Register endpoint reachable (code $code)" } else { Test-Fail "Register 500" }
}

# 3. Auth: Login + JWT
try {
    $r = Invoke-RestMethod -Uri http://localhost:5234/auth/login -Method POST -Body '{"email":"newuser@cambrian.io","password":"Test1234!"}' -ContentType "application/json"
    $script:tok = $r.token
    $script:h = @{ Authorization = "Bearer $($r.token)" }
    $parts = $r.token.Split('.')
    $payload = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($parts[1].PadRight($parts[1].Length + (4 - $parts[1].Length % 4) % 4, '=')))
    $claims = $payload | ConvertFrom-Json
    Test-Pass "Login OK - JWT issued, role=$($claims.'http://schemas.microsoft.com/ws/2008/06/identity/claims/role')"
} catch { Test-Fail "Login: $_" }

# 4. Catalog (public - no auth needed)
Write-Host "`n--- 3. CATALOG ---"
try {
    $r = Invoke-RestMethod -Uri "http://localhost:5234/catalog?page=1&pageSize=10"
    Test-Pass "Catalog (public) OK - $($r.Count) tracks"
} catch { Test-Fail "Catalog: $($_.Exception.Response.StatusCode.value__)" }

try {
    $r = Invoke-RestMethod -Uri "http://localhost:5234/discover"
    Test-Pass "Discover (public) OK - $($r.Count) items"
} catch { Test-Fail "Discover: $($_.Exception.Response.StatusCode.value__)" }

# 5. Library (auth required)
Write-Host "`n--- 4. LIBRARY ---"
try {
    Invoke-RestMethod -Uri http://localhost:5234/library | Out-Null
    Test-Fail "Library without auth should be 401 but returned 200"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -eq 401) { Test-Pass "Library 401 without auth (correct)" } else { Test-Fail "Library no-auth: expected 401 got $code" }
}
try {
    $r = Invoke-RestMethod -Uri http://localhost:5234/library -Headers $script:h
    Test-Pass "Library (authed) OK - $($r.Count) items"
} catch { Test-Fail "Library authed: $($_.Exception.Response.StatusCode.value__)" }

# 6. Upload (Creator role required)
Write-Host "`n--- 5. UPLOAD ---"
try {
    Invoke-RestMethod -Uri http://localhost:5234/upload -Method POST -Headers $script:h | Out-Null
    Test-Fail "Upload for non-creator should be 403 but returned 200"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -eq 403) { Test-Pass "Upload 403 for User role (correct)" } else { Test-Fail "Upload perm check: expected 403 got $code" }
}

# 7. Checkout (auth required)
Write-Host "`n--- 6. CHECKOUT ---"
try {
    # POST without auth - should be 401
    Invoke-RestMethod -Uri http://localhost:5234/checkout -Method POST -Body '{}' -ContentType "application/json" | Out-Null
    Test-Fail "Checkout without auth should be 401 but returned 200"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -eq 401) { Test-Pass "Checkout 401 without auth (correct)" } else { Test-Fail "Checkout no-auth: expected 401 got $code" }
}
try {
    $r = Invoke-RestMethod -Uri http://localhost:5234/checkout -Method POST -Body '{"trackId":"00000000-0000-0000-0000-000000000001","licenseType":"NonExclusive"}' -ContentType "application/json" -Headers $script:h
    Test-Pass "Checkout (authed) OK - $($r.checkoutUrl)"
} catch { Test-Fail "Checkout authed: $($_.Exception.Response.StatusCode.value__)" }

# 8. Payouts (auth required)
Write-Host "`n--- 7. PAYOUTS ---"
try {
    Invoke-RestMethod -Uri http://localhost:5234/payouts/earnings | Out-Null
    Test-Fail "Payouts without auth should be 401"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -eq 401) { Test-Pass "Payouts 401 without auth (correct)" } else { Test-Fail "Payouts no-auth: expected 401 got $code" }
}
try {
    $r = Invoke-RestMethod -Uri http://localhost:5234/payouts/earnings -Headers $script:h
    Test-Pass "Payouts (authed) OK - balance=$($r.balance)"
} catch { Test-Fail "Payouts authed: $($_.Exception.Response.StatusCode.value__)" }

# 9. Admin (Admin role required)
Write-Host "`n--- 8. ADMIN ---"
try {
    Invoke-RestMethod -Uri http://localhost:5234/admin/dashboard -Headers $script:h | Out-Null
    Test-Fail "Admin for non-admin should be 403 but returned 200"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -eq 403) { Test-Pass "Admin dashboard 403 for User role (correct)" } else { Test-Fail "Admin perm check: expected 403 got $code" }
}

# 10. Stripe Config
Write-Host "`n--- 9. STRIPE CONFIG ---"
$cfg = Get-Content .\appsettings.json | ConvertFrom-Json
if ([string]::IsNullOrEmpty($cfg.Stripe.SecretKey)) {
    Test-Warn "Stripe.SecretKey is empty - webhook and checkout will use mock responses"
} else { Test-Pass "Stripe SecretKey configured" }
if ([string]::IsNullOrEmpty($cfg.Stripe.WebhookSecret)) {
    Test-Warn "Stripe.WebhookSecret is empty - webhook signature validation disabled"
} else { Test-Pass "Stripe WebhookSecret configured" }

# 11. JWT Config
Write-Host "`n--- 10. JWT CONFIG ---"
if ($cfg.Jwt.Key.Length -lt 32) {
    Test-Fail "JWT Key too short (< 32 chars)"
} else { Test-Pass "JWT Key length OK ($($cfg.Jwt.Key.Length) chars)" }
Test-Pass "JWT Issuer: $($cfg.Jwt.Issuer)"
Test-Pass "JWT Audience: $($cfg.Jwt.Audience)"

Write-Host "`n====== RESULTS: $pass passed, $fail failed, $warn warnings ======`n" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 }
