#!/usr/bin/env pwsh
# Run the full test suite
# Usage: ./scripts/test-full.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "[test:full] Running full test suite..." -ForegroundColor Cyan
dotnet test --nologo -- -maxCpuCount
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "[test:full] All tests passed." -ForegroundColor Green
