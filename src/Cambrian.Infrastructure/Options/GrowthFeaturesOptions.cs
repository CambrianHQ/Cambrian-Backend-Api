namespace Cambrian.Infrastructure.Options;

public sealed class GrowthFeaturesOptions
{
    public bool ActivityFeedEnabled { get; set; }
    public bool TrendingV2Enabled { get; set; }
    public bool AnalyticsCaptureEnabled { get; set; }
    public bool SalesTickerEnabled { get; set; }
    public bool CheckoutV2Enabled { get; set; }
}
