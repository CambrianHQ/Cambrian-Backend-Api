namespace Cambrian.Application.Interfaces;

/// <summary>
/// Single source of truth for download entitlement.
/// All controllers must call this service — never check purchase tables inline.
/// Invariant: library membership alone does NOT grant download access.
/// </summary>
public interface IEntitlementService
{
    /// <summary>
    /// Returns true when the user has at least one completed purchase for this track.
    /// </summary>
    Task<bool> CanDownloadAsync(string userId, Guid trackId);
}
