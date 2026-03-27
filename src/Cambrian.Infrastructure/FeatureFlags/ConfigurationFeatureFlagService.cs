using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Cambrian.Infrastructure.FeatureFlags;

public sealed class ConfigurationFeatureFlagService : IFeatureFlagService
{
    private readonly GrowthFeaturesOptions _options;

    public ConfigurationFeatureFlagService(IOptions<GrowthFeaturesOptions> options)
    {
        _options = options.Value;
    }

    public Task<bool> IsEnabledAsync(string key, CancellationToken ct)
    {
        var enabled = key switch
        {
            "ActivityFeedEnabled" => _options.ActivityFeedEnabled,
            "TrendingV2Enabled" => _options.TrendingV2Enabled,
            "AnalyticsCaptureEnabled" => _options.AnalyticsCaptureEnabled,
            "SalesTickerEnabled" => _options.SalesTickerEnabled,
            "CheckoutV2Enabled" => _options.CheckoutV2Enabled,
            _ => false
        };

        return Task.FromResult(enabled);
    }
}
