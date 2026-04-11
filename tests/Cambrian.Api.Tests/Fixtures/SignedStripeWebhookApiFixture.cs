namespace Cambrian.Api.Tests.Fixtures;

/// <summary>
/// Test host that keeps the real StripeWebhookService registered so webhook
/// integration tests can verify production-style signature handling.
/// </summary>
public sealed class SignedStripeWebhookApiFixture : CambrianApiFixture
{
    public const string WebhookSecret = "whsec_test_secret";

    protected override bool UseTestWebhookService => false;

    protected override IReadOnlyDictionary<string, string?> CreateTestConfigurationOverrides() =>
        new Dictionary<string, string?>
        {
            ["Stripe:WebhookSecret"] = WebhookSecret,
        };
}
