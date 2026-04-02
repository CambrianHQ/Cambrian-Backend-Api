#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generate the OpenAPI spec from the running app and diff against the checked-in contract.

.DESCRIPTION
    Uses Swashbuckle CLI to extract the OpenAPI doc from the compiled API assembly,
    then compares it against contracts/openapi.v1.json. Returns exit code 1 if they differ.

    Usage:
      ./scripts/validate-openapi.ps1              # diff only
      ./scripts/validate-openapi.ps1 -Update      # overwrite contract with generated spec

.PARAMETER Update
    If set, overwrites contracts/openapi.v1.json with the freshly generated spec.
#>
param(
    [switch]$Update
)

$ErrorActionPreference = "Stop"

$repoRoot = (Get-Item "$PSScriptRoot/..").FullName
$apiProject = "$repoRoot/src/Cambrian.Api"
$contractPath = "$repoRoot/contracts/openapi.v1.json"
$generatedPath = "$repoRoot/contracts/openapi.generated.json"
$apiDll = "$apiProject/bin/Debug/net8.0/Cambrian.Api.dll"

# Build the project
Write-Host "Building Cambrian.Api..." -ForegroundColor Cyan
dotnet build "$apiProject" --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit 1
}

# Restore local tools (Swashbuckle CLI)
dotnet tool restore --verbosity quiet 2>$null

# Generate OpenAPI spec from the compiled assembly
# Set Testing environment + dummy connection string so the host can build
# without a real DB (startup skips migrations in Testing, and Swagger only
# needs the service provider for schema introspection).
Write-Host "Generating OpenAPI spec..." -ForegroundColor Cyan
$env:ASPNETCORE_ENVIRONMENT = "Testing"
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Database=openapi_gen;Username=unused;Password=unused"
dotnet swagger tofile --output "$generatedPath" "$apiDll" v1 2>$null
Remove-Item env:ConnectionStrings__DefaultConnection -ErrorAction SilentlyContinue
if ($LASTEXITCODE -ne 0) {
    Write-Error "OpenAPI generation failed. Swashbuckle CLI could not extract the spec."
    exit 1
}

if ($Update) {
    Copy-Item $generatedPath $contractPath -Force
    Write-Host "Updated $contractPath from generated spec." -ForegroundColor Green
    Remove-Item $generatedPath -ErrorAction SilentlyContinue
    exit 0
}

# Compare
if (-not (Test-Path $contractPath)) {
    Write-Error "Checked-in contract not found at $contractPath"
    exit 1
}

# Normalize both files (sort keys) for stable diff
$generated = Get-Content $generatedPath -Raw | ConvertFrom-Json | ConvertTo-Json -Depth 100
$contract = Get-Content $contractPath -Raw | ConvertFrom-Json | ConvertTo-Json -Depth 100

Remove-Item $generatedPath -ErrorAction SilentlyContinue

if ($generated -eq $contract) {
    Write-Host "OpenAPI contract is up to date." -ForegroundColor Green
    exit 0
} else {
    Write-Host "OpenAPI contract DIFFERS from generated spec." -ForegroundColor Red
    Write-Host "Run './scripts/validate-openapi.ps1 -Update' to update the contract." -ForegroundColor Yellow
    exit 1
}
