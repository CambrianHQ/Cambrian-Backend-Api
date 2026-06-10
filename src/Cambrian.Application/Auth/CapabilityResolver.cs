using Cambrian.Application.Interfaces;
using Cambrian.Domain.Auth;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;

namespace Cambrian.Application.Auth;

/// <summary>
/// Resolves capabilities for a user. Mirrors the frontend contract in
/// `src/lib/auth/capabilities.ts` — any divergence will silently gate UI.
/// </summary>
public sealed class CapabilityResolver : ICapabilityResolver
{
    // Retained for future capability gates that need feature-flag awareness.
    private readonly IFeatureFlagRepository _featureFlags;

    public CapabilityResolver(IFeatureFlagRepository featureFlags)
    {
        _featureFlags = featureFlags;
    }

    public Task<IReadOnlyList<string>> ResolveAsync(ApplicationUser user)
    {
        var isAdmin = string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase);
        var isCreator = string.Equals(user.Role, "Creator", StringComparison.OrdinalIgnoreCase);
        var isPro = user.CreatorTier == CreatorTier.Pro
                    || string.Equals(user.Tier, "pro", StringComparison.OrdinalIgnoreCase);

        var caps = new List<string>(capacity: 10)
        {
            Capabilities.LicensePurchase
        };

        if (isCreator || isAdmin)
        {
            caps.Add(Capabilities.TrackUpload);
            caps.Add(Capabilities.TrackEditOwn);
            caps.Add(Capabilities.TrackDeleteOwn);
            caps.Add(Capabilities.CreatorDashboardView);
        }

        if (isPro || isAdmin)
        {
            caps.Add(Capabilities.PayoutRequest);
            caps.Add(Capabilities.InvoiceDownload);
            // NOTE: track.license.exclusive / track.license.buyout are intentionally
            // NOT issued — the licensing marketplace was sunset. See residue F13.
        }

        if (isAdmin)
        {
            caps.Add(Capabilities.AdminAccess);
        }

        return Task.FromResult<IReadOnlyList<string>>(caps);
    }
}
