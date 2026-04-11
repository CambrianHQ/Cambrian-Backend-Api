param(
    [int]$Port = 5055,
    [switch]$EnableSeedData,
    [switch]$EnableDemoUsers,
    [switch]$DisableSeedData,
    [switch]$DisableDemoUsers,
    [switch]$UseExistingBuild
)

$ErrorActionPreference = "Stop"

function Get-AvailablePort {
    param(
        [int]$PreferredPort,
        [int]$MaxAttempts = 20
    )

    for ($candidate = $PreferredPort; $candidate -lt ($PreferredPort + $MaxAttempts); $candidate++) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse("127.0.0.1"), $candidate)
            $listener.Start()
            return $candidate
        }
        catch {
            continue
        }
        finally {
            if ($null -ne $listener) {
                $listener.Stop()
            }
        }
    }

    throw "No available port found in range $PreferredPort-$($PreferredPort + $MaxAttempts - 1)."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$resolvedPort = Get-AvailablePort -PreferredPort $Port
if ($resolvedPort -ne $Port) {
    Write-Host "Port $Port is already in use. Falling back to $resolvedPort."
}

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://127.0.0.1:$resolvedPort"
$env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "0"
$env:DOTNET_NOLOGO = "true"
$env:UseSharedCompilation = "false"

# Suppress Windows Event Log writes in local shells that do not have permission.
$env:Logging__EventLog__LogLevel__Default = "None"
$env:Logging__EventLog__LogLevel__Microsoft = "None"

if ($DisableSeedData) {
    $env:SeedStagingData = "false"
} else {
    $env:SeedStagingData = "true"
}

if ($DisableDemoUsers) {
    # Whitespace is treated as empty by the app's seed guard.
    $env:SeedDemoUsers__Password = " "
} else {
    $env:SeedDemoUsers__Password = "Cambrian!Dev12345"
}

Write-Host "Starting Cambrian backend on http://127.0.0.1:$resolvedPort"
Write-Host "Seed staging data: $($env:SeedStagingData)"
Write-Host "Seed demo users: $([string]::IsNullOrWhiteSpace($env:SeedDemoUsers__Password) -eq $false)"

if (-not [string]::IsNullOrWhiteSpace($env:SeedDemoUsers__Password)) {
    Write-Host ""
    Write-Host "Demo credentials"
    Write-Host "Password: $($env:SeedDemoUsers__Password)"
    Write-Host "Creators:"
    Write-Host "  aiden@cambrianmusic.com"
    Write-Host "  bellanova@cambrianmusic.com"
    Write-Host "  cassius@cambrianmusic.com"
    Write-Host "  dahlia@cambrianmusic.com"
    Write-Host "  ezra@cambrianmusic.com"
    Write-Host "  faye@cambrianmusic.com"
    Write-Host "  griffin@cambrianmusic.com"
    Write-Host "  harper@cambrianmusic.com"
    Write-Host "  indigo@cambrianmusic.com"
    Write-Host "  juniper@cambrianmusic.com"

    if ($env:SeedStagingData -eq "true") {
        Write-Host "Listeners and edge cases:"
        Write-Host "  listener-free@cambrianmusic.com"
        Write-Host "  listener-paid@cambrianmusic.com"
        Write-Host "  listener-heavy@cambrianmusic.com"
        Write-Host "  creator-noprofile@cambrianmusic.com"
    }

    Write-Host ""
}

try {
    dotnet build-server shutdown | Out-Null
}
catch {
    Write-Host "Build server shutdown skipped: $($_.Exception.Message)"
}

if ($UseExistingBuild) {
    $candidateDll = Get-ChildItem "src/Cambrian.Api/bin" -Recurse -Filter "Cambrian.Api.dll" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -ne $candidateDll) {
        $sourceDir = $candidateDll.Directory.FullName
        $runtimeDir = Join-Path $repoRoot ".codex-run/backend-runtime"

        if (Test-Path $runtimeDir) {
            Remove-Item -Recurse -Force $runtimeDir
        }

        New-Item -ItemType Directory -Path $runtimeDir | Out-Null
        Copy-Item -Path (Join-Path $sourceDir "*") -Destination $runtimeDir -Recurse -Force

        Write-Host "Using existing build: $($candidateDll.FullName)"
        Write-Host "Runtime copy: $runtimeDir"

        Push-Location $runtimeDir
        try {
            dotnet Cambrian.Api.dll
        }
        finally {
            Pop-Location
        }
        return
    }

    Write-Host "No existing Cambrian.Api.dll build found. Falling back to dotnet run."
}

dotnet run --project src/Cambrian.Api/Cambrian.Api.csproj --no-launch-profile
