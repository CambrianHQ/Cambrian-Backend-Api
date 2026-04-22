namespace Cambrian.Domain.Auth;

/// <summary>
/// Defines all capability constants for the capability-based authorization system.
/// These are the single source of truth for all permissions in the system.
/// </summary>
public static class Capabilities
{
    public const string TrackUpload = "track.upload";
    public const string TrackEditOwn = "track.edit.own";
    public const string TrackDeleteOwn = "track.delete.own";
    public const string LicensePurchase = "license.purchase";
    public const string PayoutRequest = "payout.request";
    public const string AdminAccess = "admin.access";

    /// <summary>All capabilities that exist in the system.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        TrackUpload,
        TrackEditOwn,
        TrackDeleteOwn,
        LicensePurchase,
        PayoutRequest,
        AdminAccess
    };
}
