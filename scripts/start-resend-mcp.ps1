param(
    [string]$ApiKey = "",
    [string]$SenderEmail = "",
    [string]$ReplyToEmail = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$resolvedApiKey = if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey
} elseif (-not [string]::IsNullOrWhiteSpace($env:RESEND_API_KEY)) {
    $env:RESEND_API_KEY
} else {
    $env:Email__ResendApiKey
}

if ([string]::IsNullOrWhiteSpace($resolvedApiKey)) {
    throw "RESEND_API_KEY must be set (or pass -ApiKey) before starting the Resend MCP server."
}

$resolvedSenderEmail = if (-not [string]::IsNullOrWhiteSpace($SenderEmail)) {
    $SenderEmail
} elseif (-not [string]::IsNullOrWhiteSpace($env:SENDER_EMAIL_ADDRESS)) {
    $env:SENDER_EMAIL_ADDRESS
} else {
    $env:Email__FromAddress
}

$resolvedReplyToEmail = if (-not [string]::IsNullOrWhiteSpace($ReplyToEmail)) {
    $ReplyToEmail
} else {
    $env:REPLY_TO_EMAIL_ADDRESS
}

if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
    throw "npx is required to launch resend-mcp. Install Node.js 18+ and try again."
}

$env:RESEND_API_KEY = $resolvedApiKey

if (-not [string]::IsNullOrWhiteSpace($resolvedSenderEmail)) {
    $env:SENDER_EMAIL_ADDRESS = $resolvedSenderEmail
}

if (-not [string]::IsNullOrWhiteSpace($resolvedReplyToEmail)) {
    $env:REPLY_TO_EMAIL_ADDRESS = $resolvedReplyToEmail
}

Write-Host "Starting Resend MCP server from $repoRoot"
if (-not [string]::IsNullOrWhiteSpace($resolvedSenderEmail)) {
    Write-Host "Default sender: $resolvedSenderEmail"
}

& npx -y resend-mcp
exit $LASTEXITCODE
