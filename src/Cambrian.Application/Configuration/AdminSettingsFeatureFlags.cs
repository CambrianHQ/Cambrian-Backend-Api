namespace Cambrian.Application.Configuration;

/// <summary>
/// Names of the DB-backed FeatureFlag rows that back Admin Settings' runtime toggles.
/// Seeded to Enabled=true by the SeedAdminSettingsFeatureFlags migration so behavior
/// doesn't regress from the previous hardcoded-true values on deploy.
/// </summary>
public static class AdminSettingsFeatureFlags
{
    public const string PayoutsEnabled = "PayoutsEnabled";
    public const string ModerationEnabled = "ModerationEnabled";
    public const string MarketplaceEnabled = "MarketplaceEnabled";
    public const string AllowExclusiveListings = "AllowExclusiveListings";
    public const string RequireTrackReview = "RequireTrackReview";

    public static readonly string[] All =
    {
        PayoutsEnabled,
        ModerationEnabled,
        MarketplaceEnabled,
        AllowExclusiveListings,
        RequireTrackReview,
    };
}
