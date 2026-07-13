param(
    [Parameter(Mandatory = $true)]
    [string]$EventId,

    [Parameter(Mandatory = $true)]
    [string]$WebhookEndpointId,

    [string]$StripeApiKey = $env:STRIPE_SECRET_KEY
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command stripe -ErrorAction SilentlyContinue)) {
    throw "Stripe CLI is not installed or is not on PATH."
}

if ([string]::IsNullOrWhiteSpace($StripeApiKey) -or -not $StripeApiKey.StartsWith("sk_test_")) {
    throw "STRIPE_SECRET_KEY must be a Stripe test-mode secret key (sk_test_...)."
}

if (-not $EventId.StartsWith("evt_") -or -not $WebhookEndpointId.StartsWith("we_")) {
    throw "EventId must start with evt_ and WebhookEndpointId must start with we_."
}

Write-Host "Resending the same Stripe test-mode event twice to verify staging idempotency..."

1..2 | ForEach-Object {
    & stripe events resend $EventId --webhook-endpoint $WebhookEndpointId --api-key $StripeApiKey
    if ($LASTEXITCODE -ne 0) {
        throw "Stripe CLI resend attempt $_ failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Both deliveries were accepted by Stripe. Verify the staging StripeWebhookEvents row is completed and the related fulfillment row exists exactly once."
