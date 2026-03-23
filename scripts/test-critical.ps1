#!/usr/bin/env pwsh
# Run only critical tests (auth, purchase, upload, library, contracts, DB integrity)
# Usage: ./scripts/test-critical.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "[test:critical] Running critical test suite..." -ForegroundColor Cyan
dotnet test --nologo --filter Category=Critical -- -maxCpuCount
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "[test:critical] All critical tests passed." -ForegroundColor Green
