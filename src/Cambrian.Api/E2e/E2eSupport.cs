using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Cambrian.Api.E2e;

/// <summary>
/// Gate + crypto helpers for the test-only E2E support surface (<c>/__e2e/*</c>).
///
/// The surface is mapped ONLY in the <c>Testing</c> environment or in local
/// <c>Development</c> when <see cref="EnabledConfigKey"/> is explicitly true. It is
/// NEVER mapped in Production or Staging — the deployed money environments are denied
/// even if a flag is mis-set. Every request is additionally authenticated with a
/// constant-time-compared shared secret (<see cref="SecretHeader"/>).
/// </summary>
public static class E2eSupport
{
    public const string EnabledConfigKey = "Cambrian:E2E:Enabled";
    public const string SecretConfigKey = "Cambrian:E2E:Secret";
    public const string SecretEnvVar = "CAMBRIAN_E2E_SECRET";
    public const string SecretHeader = "x-e2e-secret";

    private const string TestingEnvironment = "Testing";
    private const string StagingEnvironment = "Staging";

    /// <summary>
    /// True only when the E2E surface may be exposed: <c>Testing</c> always;
    /// <c>Development</c> iff <see cref="EnabledConfigKey"/> is true; never Production/Staging.
    /// </summary>
    public static bool IsEnabled(IHostEnvironment env, IConfiguration cfg)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(cfg);

        // Hard deny for the deployed environments, regardless of any flag. This is the
        // load-bearing safety property: the destructive reset/seed surface must never be
        // reachable in Production or Staging.
        if (env.IsProduction() || env.IsEnvironment(StagingEnvironment))
            return false;

        if (env.IsEnvironment(TestingEnvironment))
            return true;

        if (env.IsDevelopment())
            return cfg.GetValue<bool>(EnabledConfigKey);

        return false;
    }

    /// <summary>
    /// Resolve the configured E2E secret. Config key takes precedence; the
    /// <c>CAMBRIAN_E2E_SECRET</c> environment variable is the documented fallback
    /// (and also binds to <see cref="SecretConfigKey"/> via the <c>Cambrian__E2E__Secret</c> form).
    /// </summary>
    public static string? ResolveSecret(IConfiguration cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return cfg[SecretConfigKey] ?? Environment.GetEnvironmentVariable(SecretEnvVar);
    }

    /// <summary>
    /// Constant-time secret comparison. Both sides are hashed to a fixed 32-byte digest
    /// first, so neither the comparison time nor the input length leaks and
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> always receives equal-length
    /// spans. An empty/missing expected secret denies all requests (fail closed).
    /// </summary>
    public static bool SecretMatches(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
            return false;

        Span<byte> providedHash = stackalloc byte[32];
        Span<byte> expectedHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(provided), providedHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), expectedHash);
        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }

    /// <summary>
    /// Build a Stripe-compatible signature header (<c>t=&lt;ts&gt;,v1=&lt;hmac&gt;</c>) for a
    /// payload using the webhook signing secret. Mirrors
    /// <c>StripeWebhookService.ValidateStripeSignature</c> exactly so the E2E surface can feed
    /// synthetic events through the REAL webhook handler — exercising signature verification,
    /// event-id dedup and every state transition — with no calls to Stripe.
    /// </summary>
    /// <param name="unixTimestampSeconds">
    /// Current unix time in seconds. The real verifier rejects timestamps more than 300s from
    /// now, so callers must pass <c>DateTimeOffset.UtcNow.ToUnixTimeSeconds()</c>.
    /// </param>
    public static string SignStripePayload(string payload, string webhookSecret, long unixTimestampSeconds)
    {
        var signedPayload = $"{unixTimestampSeconds}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret));
        var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload)))
            .ToLowerInvariant();
        return $"t={unixTimestampSeconds},v1={hex}";
    }
}
