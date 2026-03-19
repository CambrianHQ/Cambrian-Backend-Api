using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class TierService : ITierService
{
    public object GetTierConfig()
    {
        var configs = TierManifest.All.Select(t => new
        {
            tier = t.Slug,
            displayName = t.DisplayName,
            uploadLimit = t.UploadLimit,
            feeRate = t.FeeRate,
            priceCents = t.PriceCents,
            features = t.Features,
            analyticsAccess = t.AnalyticsAccess.ToString().ToLowerInvariant()
        });

        return new
        {
            version = TierManifest.ContractVersion,
            tiers = configs
        };
    }
}
