namespace Cambrian.Domain.Auth;

/// <summary>
/// Single source of truth for capability strings.
/// Frontend contract: `src/lib/auth/capabilities.ts`. Strings not in the
/// frontend list are ignored client-side but may still gate backend policies.
/// </summary>
public static class Capabilities
{
    // Frontend-facing
    public const string LicensePurchase = "license.purchase";
    public const string TrackUpload = "track.upload";
    public const string TrackEditOwn = "track.edit.own";
    public const string TrackLicenseExclusive = "track.license.exclusive";
    public const string TrackLicenseBuyout = "track.license.buyout";
    public const string PayoutRequest = "payout.request";
    public const string CreatorDashboardView = "creator.dashboard.view";
    public const string InvoiceDownload = "invoice.download";
    public const string AdminAccess = "admin.access";

    // Backend-only — gates server policies, ignored by the frontend.
    public const string TrackDeleteOwn = "track.delete.own";

    public static readonly IReadOnlyList<string> All = new[]
    {
        LicensePurchase,
        TrackUpload,
        TrackEditOwn,
        TrackDeleteOwn,
        TrackLicenseExclusive,
        TrackLicenseBuyout,
        PayoutRequest,
        CreatorDashboardView,
        InvoiceDownload,
        AdminAccess
    };
}
