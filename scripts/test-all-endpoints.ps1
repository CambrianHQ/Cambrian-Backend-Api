#!/usr/bin/env pwsh
# Manual endpoint smoke-test script
# Tests every endpoint from endpoint-manifest.v1.json against http://localhost:5000

$ErrorActionPreference = "Continue"
$base = "http://localhost:5000"
$results = @()

function Test-Endpoint {
    param(
        [string]$Method,
        [string]$Path,
        [string]$Token = "",
        [string]$Body = "",
        [string]$Tag = "",
        [string]$ContentType = "application/json",
        [int]$ExpectedMin = 200,
        [int]$ExpectedMax = 499
    )

    $headers = @{}
    if ($Token) { $headers["Authorization"] = "Bearer $Token" }

    try {
        $params = @{
            Uri             = "$base$Path"
            Method          = $Method
            Headers         = $headers
            TimeoutSec      = 10
            UseBasicParsing = $true
        }
        if ($Body -and $Method -notin @("GET","HEAD","DELETE")) {
            $params["Body"] = $Body
            $params["ContentType"] = $ContentType
        }

        $resp = Invoke-WebRequest @params
        $status = [int]$resp.StatusCode
    }
    catch {
        if ($_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
        }
        else {
            $status = 0
        }
    }

    $ok = ($status -ge $ExpectedMin -and $status -le $ExpectedMax)
    $icon = if ($ok) { "PASS" } else { "FAIL" }

    $script:results += [PSCustomObject]@{
        Tag    = $Tag
        Method = $Method
        Path   = $Path
        Status = $status
        Result = $icon
    }

    Write-Host "$icon  $status  $Method $Path  [$Tag]"
    return $status
}

Write-Host "==============================================="
Write-Host "  CAMBRIAN BACKEND — FULL ENDPOINT SMOKE TEST"
Write-Host "  Target: $base"
Write-Host "===============================================`n"

# ═══════════════════════════════════════
# 1. ANONYMOUS ENDPOINTS
# ═══════════════════════════════════════
Write-Host "── ANONYMOUS ENDPOINTS ──`n"

Test-Endpoint -Method GET -Path "/health" -Tag "Health"
Test-Endpoint -Method GET -Path "/health/storage" -Tag "Health"
Test-Endpoint -Method GET -Path "/auth/health" -Tag "Auth"
Test-Endpoint -Method GET -Path "/discover" -Tag "Catalog"
Test-Endpoint -Method GET -Path "/catalog" -Tag "Catalog"
Test-Endpoint -Method GET -Path "/trending" -Tag "Catalog"
Test-Endpoint -Method GET -Path "/community" -Tag "Community"
Test-Endpoint -Method GET -Path "/tiers/config" -Tag "Tiers"
Test-Endpoint -Method GET -Path "/payments/result" -Tag "Payments"

# Track detail with fake ID (expect 404)
Test-Endpoint -Method GET -Path "/tracks/00000000-0000-0000-0000-000000000001" -Tag "Catalog" -ExpectedMin 200 -ExpectedMax 404

# Creator identity public endpoints
Test-Endpoint -Method GET -Path "/api/creators/00000000-0000-0000-0000-000000000001" -Tag "Creators" -ExpectedMin 200 -ExpectedMax 404
Test-Endpoint -Method GET -Path "/api/creators/by-username/nonexistent" -Tag "Creators" -ExpectedMin 200 -ExpectedMax 404
Test-Endpoint -Method GET -Path "/api/creators/resolve/nonexistent" -Tag "Creators" -ExpectedMin 200 -ExpectedMax 404
Test-Endpoint -Method GET -Path "/api/creators/00000000-0000-0000-0000-000000000001/tracks" -Tag "Creators" -ExpectedMin 200 -ExpectedMax 404
Test-Endpoint -Method GET -Path "/api/creators/username-availability?username=testcheck" -Tag "Creators"

# Creator profile public endpoints
Test-Endpoint -Method GET -Path "/creator-profile/nonexistent-slug" -Tag "CreatorProfile" -ExpectedMin 200 -ExpectedMax 404
Test-Endpoint -Method GET -Path "/creator-profile/nonexistent-slug/storefront" -Tag "CreatorProfile" -ExpectedMin 200 -ExpectedMax 404
Test-Endpoint -Method GET -Path "/creator-profile/nonexistent-slug/collections" -Tag "CreatorProfile" -ExpectedMin 200 -ExpectedMax 404

# Auth public endpoints (POST with bodies)
Test-Endpoint -Method POST -Path "/auth/forgot-password" -Tag "Auth" -Body '{"email":"nobody@example.com"}'
Test-Endpoint -Method POST -Path "/auth/verify-code" -Tag "Auth" -Body '{"email":"nobody@example.com","code":"12345678"}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/auth/reset-password" -Tag "Auth" -Body '{"email":"nobody@example.com","code":"12345678","newPassword":"NewPass1234!@"}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/auth/recover-username" -Tag "Auth" -Body '{"email":"nobody@example.com"}'

# Webhook (needs Stripe signature — expect 400 not 500)
Test-Endpoint -Method POST -Path "/webhook/stripe" -Tag "Webhook" -Body '{}' -ExpectedMin 200 -ExpectedMax 499

# ═══════════════════════════════════════
# 2. REGISTER + LOGIN
# ═══════════════════════════════════════
Write-Host "`n── AUTH FLOW ──`n"

$uid = [guid]::NewGuid().ToString("N").Substring(0,8)
$testEmail = "smoke-$uid@test.com"
$testPass = "Test1234!@"

$regStatus = Test-Endpoint -Method POST -Path "/auth/register" -Tag "Auth" -Body "{`"email`":`"$testEmail`",`"password`":`"$testPass`",`"displayName`":`"SmokeTest`"}"

# Login
$loginResp = Invoke-RestMethod -Uri "$base/auth/login" -Method POST -Body (@{ email=$testEmail; password=$testPass } | ConvertTo-Json) -ContentType "application/json"
$userToken = $loginResp.data.token
Write-Host "LOGIN: OK | userId=$($loginResp.data.user.id)"

# Login as demo creator
try {
    $cResp = Invoke-RestMethod -Uri "$base/auth/login" -Method POST -Body (@{ email="creator1@cambrian-demo.local"; password="Cambrian2026!" } | ConvertTo-Json) -ContentType "application/json"
    $creatorToken = $cResp.data.token
    Write-Host "CREATOR LOGIN: OK | role=$($cResp.data.user.role) tier=$($cResp.data.user.tier)"
} catch {
    Write-Host "CREATOR LOGIN: FAILED (using user token)"
    $creatorToken = $userToken
}

# Login as admin
try {
    $aResp = Invoke-RestMethod -Uri "$base/auth/login" -Method POST -Body (@{ email="admin@cambrian.local"; password="Admin1234!" } | ConvertTo-Json) -ContentType "application/json"
    $adminToken = $aResp.data.token
    Write-Host "ADMIN LOGIN: OK | role=$($aResp.data.user.role)"
} catch {
    Write-Host "ADMIN LOGIN: FAILED (using user token)"
    $adminToken = $userToken
}

# ═══════════════════════════════════════
# 3. AUTHENTICATED USER ENDPOINTS
# ═══════════════════════════════════════
Write-Host "`n── AUTHENTICATED (User) ──`n"

Test-Endpoint -Method GET -Path "/auth/me" -Token $userToken -Tag "Auth"
Test-Endpoint -Method POST -Path "/auth/logout" -Token $userToken -Tag "Auth"
Test-Endpoint -Method GET -Path "/auth/csrf-token" -Tag "Auth" -ExpectedMin 200 -ExpectedMax 499

# Library
Test-Endpoint -Method GET -Path "/library" -Token $userToken -Tag "Library"
Test-Endpoint -Method GET -Path "/library/purchased-track-ids" -Token $userToken -Tag "Library"

# Wallet
Test-Endpoint -Method GET -Path "/wallet" -Token $userToken -Tag "Wallet"
Test-Endpoint -Method GET -Path "/wallet/history" -Token $userToken -Tag "Wallet"

# Invoices
Test-Endpoint -Method GET -Path "/invoices" -Token $userToken -Tag "Invoices"

# Payments
Test-Endpoint -Method GET -Path "/payments/state" -Token $userToken -Tag "Payments"

# Subscriptions
Test-Endpoint -Method GET -Path "/subscriptions/plans" -Token $userToken -Tag "Subscriptions"
Test-Endpoint -Method GET -Path "/subscriptions/current" -Token $userToken -Tag "Subscriptions"
Test-Endpoint -Method GET -Path "/subscriptions/history" -Token $userToken -Tag "Subscriptions"

# Analytics
Test-Endpoint -Method GET -Path "/analytics/summary" -Token $userToken -Tag "Analytics"
Test-Endpoint -Method GET -Path "/analytics/events" -Token $userToken -Tag "Analytics"

# Billing
Test-Endpoint -Method GET -Path "/billing/status" -Token $userToken -Tag "Billing"

# Settings
Test-Endpoint -Method GET -Path "/settings/profile" -Token $userToken -Tag "Settings"

# Data
Test-Endpoint -Method GET -Path "/data/account" -Token $userToken -Tag "Data"

# Stream (no active session expected)
Test-Endpoint -Method GET -Path "/stream" -Token $userToken -Tag "Stream" -ExpectedMin 200 -ExpectedMax 499

# Payouts
Test-Endpoint -Method GET -Path "/payouts/connect-status" -Token $userToken -Tag "Payouts"
Test-Endpoint -Method GET -Path "/payouts/account" -Token $userToken -Tag "Payouts"
Test-Endpoint -Method GET -Path "/payouts/earnings" -Token $userToken -Tag "Payouts"
Test-Endpoint -Method GET -Path "/payouts/history" -Token $userToken -Tag "Payouts"
Test-Endpoint -Method GET -Path "/earnings" -Token $userToken -Tag "Payouts"

# Licenses
Test-Endpoint -Method GET -Path "/licenses" -Token $userToken -Tag "Licenses"

# Feature Flags
Test-Endpoint -Method GET -Path "/feature-flags" -Token $userToken -Tag "FeatureFlags"
Test-Endpoint -Method GET -Path "/feature-flags/check/storefront-v2" -Token $userToken -Tag "FeatureFlags"

# Tracks (authenticated)
Test-Endpoint -Method GET -Path "/tracks" -Token $userToken -Tag "Tracks" -ExpectedMin 200 -ExpectedMax 499

# Users
Test-Endpoint -Method GET -Path "/users/$testEmail" -Token $userToken -Tag "Users" -ExpectedMin 200 -ExpectedMax 404

# POST endpoints that need bodies
Test-Endpoint -Method POST -Path "/analytics/track" -Token $userToken -Tag "Analytics" -Body '{"eventType":"page_view","trackId":null}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/billing/checkout" -Token $userToken -Tag "Billing" -Body '{"plan":"pro"}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/billing/checkout-session" -Token $userToken -Tag "Billing" -Body '{"plan":"pro"}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/settings/password" -Token $userToken -Tag "Settings" -Body '{"currentPassword":"Test1234!@","newPassword":"Test1234!@"}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/stream/start" -Token $userToken -Tag "Stream" -Body '{"trackId":"00000000-0000-0000-0000-000000000001"}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/stream/stop" -Token $userToken -Tag "Stream" -Body '{"sessionId":"00000000-0000-0000-0000-000000000001"}' -ExpectedMin 200 -ExpectedMax 499

# ═══════════════════════════════════════
# 4. CREATOR ENDPOINTS
# ═══════════════════════════════════════
Write-Host "`n── CREATOR ENDPOINTS ──`n"

Test-Endpoint -Method GET -Path "/creator/tracks" -Token $creatorToken -Tag "Creator"
Test-Endpoint -Method GET -Path "/creator/revenue" -Token $creatorToken -Tag "Creator"
Test-Endpoint -Method GET -Path "/creator-profile/me" -Token $creatorToken -Tag "CreatorProfile" -ExpectedMin 200 -ExpectedMax 404
Test-Endpoint -Method PUT -Path "/creator-profile/me" -Token $creatorToken -Tag "CreatorProfile" -Body "{`"slug`":`"smoke-$uid`",`"bio`":`"smoke test`",`"showEarnings`":false,`"showDownloadStats`":false}" -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/creator-profile/me/collections" -Token $creatorToken -Tag "CreatorProfile" -Body '{"title":"Smoke Collection","description":"test","trackIds":[]}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method PUT -Path "/api/creator/me" -Token $creatorToken -Tag "Creators" -Body '{"displayName":"SmokeCreator","bio":"test"}' -ExpectedMin 200 -ExpectedMax 499

# AI endpoints
Test-Endpoint -Method GET -Path "/ai/playlist" -Token $creatorToken -Tag "Ai" -ExpectedMin 200 -ExpectedMax 499

# Upload (no file, expect 400)
Test-Endpoint -Method POST -Path "/upload" -Token $creatorToken -Tag "Upload" -Body '{}' -ExpectedMin 200 -ExpectedMax 499

# ═══════════════════════════════════════
# 5. ADMIN ENDPOINTS
# ═══════════════════════════════════════
Write-Host "`n── ADMIN ENDPOINTS ──`n"

Test-Endpoint -Method GET -Path "/admin/dashboard" -Token $adminToken -Tag "Admin"
Test-Endpoint -Method GET -Path "/admin/audit" -Token $adminToken -Tag "Admin"
Test-Endpoint -Method GET -Path "/admin/settings" -Token $adminToken -Tag "Admin"
Test-Endpoint -Method GET -Path "/admin/payouts/requests" -Token $adminToken -Tag "Admin"
Test-Endpoint -Method GET -Path "/admin/users" -Token $adminToken -Tag "Admin"
Test-Endpoint -Method GET -Path "/admin/reports" -Token $adminToken -Tag "Admin"
Test-Endpoint -Method GET -Path "/admin/integrity" -Token $adminToken -Tag "Admin"

# Admin POST endpoints with fake IDs (expect 4xx not 5xx)
$fakeId = "00000000-0000-0000-0000-000000000001"
Test-Endpoint -Method POST -Path "/admin/settings" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/collections/curate" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/tags/manage" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/users/$fakeId/role" -Token $adminToken -Tag "Admin" -Body '{"role":"User"}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/users/$fakeId/suspend" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/users/$fakeId/reactivate" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/users/$fakeId/reset-password" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/users/$fakeId/verify-creator" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/reports/$fakeId/investigate" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/tracks/$fakeId/remove" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/tracks/$fakeId/restore" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/tracks/$fakeId/hide" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/tracks/$fakeId/flag" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/tracks/$fakeId/feature" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/tracks/$fakeId/pin" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/tracks/$fakeId/visibility" -Token $adminToken -Tag "Admin" -Body '{"visibility":"hidden"}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/payouts/$fakeId/approve" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499
Test-Endpoint -Method POST -Path "/admin/payouts/$fakeId/reject" -Token $adminToken -Tag "Admin" -Body '{}' -ExpectedMin 200 -ExpectedMax 499

# ═══════════════════════════════════════
# SUMMARY
# ═══════════════════════════════════════
Write-Host "`n==============================================="
Write-Host "  RESULTS SUMMARY"
Write-Host "===============================================`n"

$passed = ($results | Where-Object { $_.Result -eq "PASS" }).Count
$failed = ($results | Where-Object { $_.Result -eq "FAIL" }).Count
$total = $results.Count

Write-Host "Total:  $total"
Write-Host "Passed: $passed"
Write-Host "Failed: $failed`n"

if ($failed -gt 0) {
    Write-Host "── FAILURES ──"
    $results | Where-Object { $_.Result -eq "FAIL" } | Format-Table -Property Result, Status, Method, Path, Tag -AutoSize
}

Write-Host "`n── STATUS DISTRIBUTION ──"
$results | Group-Object Status | Sort-Object Name | Format-Table -Property @{N="HTTP Status"; E={$_.Name}}, Count -AutoSize

Write-Host "── BY TAG ──"
$results | Group-Object Tag | ForEach-Object {
    $pass = ($_.Group | Where-Object { $_.Result -eq "PASS" }).Count
    $fail = ($_.Group | Where-Object { $_.Result -eq "FAIL" }).Count
    [PSCustomObject]@{ Tag=$_.Name; Total=$_.Count; Pass=$pass; Fail=$fail }
} | Sort-Object Tag | Format-Table -AutoSize
