namespace Cambrian.Application.DTOs.Admin;

/// <summary>Read-only snapshot of one TierManifest tier, for the admin Settings "plan manifest" display.</summary>
public class AdminTierManifestEntry
{
    public string Slug { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int PriceCents { get; set; }

    public double FeeRate { get; set; }

    /// <summary>Null = unlimited.</summary>
    public int? UploadLimit { get; set; }

    public int ReleaseReadyCreditsPerMonth { get; set; }

    public IReadOnlyDictionary<string, bool> FeatureFlags { get; set; } = new Dictionary<string, bool>();
}

/// <summary>The 5 DB-backed feature flags that back Admin Settings' editable toggles.</summary>
public class AdminRuntimeSettings
{
    public bool PayoutsEnabled { get; set; }
    public bool ModerationEnabled { get; set; }
    public bool MarketplaceEnabled { get; set; }
    public bool AllowExclusiveListings { get; set; }
    public bool RequireTrackReview { get; set; }
}

public class AdminSettingsResponse
{
    /// <summary>Read-only — compile-time TierManifest constants, no Save affordance for these.</summary>
    public IReadOnlyList<AdminTierManifestEntry> PlanManifest { get; set; } = new List<AdminTierManifestEntry>();

    public AdminRuntimeSettings RuntimeSettings { get; set; } = new();
}

/// <summary>Request body for POST /admin/settings. Only the 5 runtime toggles are accepted —
/// plan-manifest fields are structurally absent, not merely ignored.</summary>
public class AdminSettingsUpdateRequest
{
    public bool PayoutsEnabled { get; set; }
    public bool ModerationEnabled { get; set; }
    public bool MarketplaceEnabled { get; set; }
    public bool AllowExclusiveListings { get; set; }
    public bool RequireTrackReview { get; set; }
}
