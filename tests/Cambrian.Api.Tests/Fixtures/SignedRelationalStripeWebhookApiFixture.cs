using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Cambrian.Api.Tests.Fixtures;

/// <summary>Real application container + real webhook services + PostgreSQL/relational fixture.</summary>
public sealed class SignedRelationalStripeWebhookApiFixture : RelationalCambrianApiFixture
{
    public const string PlatformWebhookSecret = "whsec_relational_platform_test";
    public const string ConnectWebhookSecret = "whsec_relational_connect_test";

    protected override bool UseTestWebhookService => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Stripe:WebhookSecret"] = PlatformWebhookSecret,
                ["Stripe:ConnectWebhookSecret"] = ConnectWebhookSecret,
            }));
    }
}
