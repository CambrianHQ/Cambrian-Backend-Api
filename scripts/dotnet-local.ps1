$ErrorActionPreference = "Stop"
$DotnetArgs = @($args)

$repoRoot = Split-Path -Parent $PSScriptRoot
$localDotnetHome = Join-Path $repoRoot ".codex-run\\dotnet"
$restoreConfig = Join-Path $repoRoot "NuGet.Config"

New-Item -ItemType Directory -Force -Path $localDotnetHome | Out-Null

$env:DOTNET_CLI_HOME = $localDotnetHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "false"
$env:DOTNET_NOLOGO = "1"

if (-not $DotnetArgs -or $DotnetArgs.Count -eq 0) {
    $DotnetArgs = @("build", "Cambrian.sln")
}

$forwardedArgs = @(
    "-nr:false",
    "-m:1",
    "-p:UseSharedCompilation=false",
    "-p:RestoreConfigFile=$restoreConfig"
)

& dotnet @DotnetArgs @forwardedArgs
exit $LASTEXITCODE
