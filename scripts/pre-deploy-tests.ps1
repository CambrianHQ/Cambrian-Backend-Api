<#
.SYNOPSIS
    Pre-deploy integration test runner for Cambrian Backend API.
    Run this before every deploy to catch regressions.

.DESCRIPTION
    Executes all integration tests (Auth, Purchase, Library, Upload, Download)
    against an in-memory test server (no database or external services required).

.EXAMPLE
    .\scripts\pre-deploy-tests.ps1
    .\scripts\pre-deploy-tests.ps1 -Filter "Auth"
    .\scripts\pre-deploy-tests.ps1 -Verbose
#>

param(
    [string]$Filter = "",
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host ""
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "  Cambrian Pre-Deploy Integration Tests" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

$testProject = Join-Path $root "tests\Cambrian.Api.Tests\Cambrian.Api.Tests.csproj"

if (-not (Test-Path $testProject)) {
    Write-Host "ERROR: Test project not found at $testProject" -ForegroundColor Red
    exit 1
}

# Build the test filter argument
$filterArg = @()
if ($Filter) {
    $filterArg = @("--filter", "FullyQualifiedName~$Filter")
}

$verbosityArg = if ($Verbose) { "--verbosity", "detailed" } else { "--verbosity", "normal" }

Write-Host "[1/2] Building solution..." -ForegroundColor Yellow
dotnet build $testProject --configuration Release --nologo --no-restore 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "BUILD FAILED — fix compilation errors before deploying." -ForegroundColor Red
    dotnet build $testProject --configuration Release --nologo
    exit 1
}
Write-Host "      Build succeeded." -ForegroundColor Green

Write-Host ""
Write-Host "[2/2] Running integration tests..." -ForegroundColor Yellow
Write-Host ""

dotnet test $testProject `
    --configuration Release `
    --no-build `
    --nologo `
    @verbosityArg `
    @filterArg `
    --logger "console;verbosity=normal"

$testResult = $LASTEXITCODE

Write-Host ""
Write-Host "=============================================" -ForegroundColor Cyan

if ($testResult -eq 0) {
    Write-Host "  ALL TESTS PASSED — safe to deploy" -ForegroundColor Green
} else {
    Write-Host "  TESTS FAILED — do NOT deploy" -ForegroundColor Red
}

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

exit $testResult
