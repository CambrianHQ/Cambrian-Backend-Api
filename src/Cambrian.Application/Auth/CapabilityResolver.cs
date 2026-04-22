using Cambrian.Application.Interfaces;
using Cambrian.Domain.Auth;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Auth;

/// <summary>
/// Resolves capabilities for a user based on their state in the database.
/// </summary>
public sealed class CapabilityResolver : ICapabilityResolver
{
    private readonly IFeatureFlagRepository _featureFlags;

    public CapabilityResolver(IFeatureFlagRepository featureFlags)
    {
        _featureFlags = featureFlags;
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(ApplicationUser user)
    {
        // Admin gets all capabilities
        if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            return Capabilities.All;

        var caps = new List<string>();

        // Every authenticated user can purchase licenses
        caps.Add(Capabilities.LicensePurchase);

        // Track upload: user has Creator role or has set a username (creator onboarding complete)
        var isCreator = string.Equals(user.Role, "Creator", StringComparison.OrdinalIgnoreCase);
        var hasUsername = !string.IsNullOrWhiteSpace(user.UserName)
                         && !string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase);

        if (isCreator || hasUsername)
        {
            caps.Add(Capabilities.TrackUpload);
            caps.Add(Capabilities.TrackEditOwn);
            caps.Add(Capabilities.TrackDeleteOwn);
        }

        // Payout: requires Stripe Connect account AND the feature flag is enabled
        if (!string.IsNullOrWhiteSpace(user.StripeAccountId))
        {
            var stripeConnectEnabled = await _featureFlags.IsEnabledAsync("StripeConnectEnabled");
            if (stripeConnectEnabled)
            {
                caps.Add(Capabilities.PayoutRequest);
            }
        }

        return caps;
    }
}
