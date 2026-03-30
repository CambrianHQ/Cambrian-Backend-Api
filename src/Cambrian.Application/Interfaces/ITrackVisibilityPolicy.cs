namespace Cambrian.Application.Interfaces;

/// <summary>
/// Single source of truth for track read/stream access based on visibility.
/// All controllers must call this policy — never inline the visibility check.
/// Invariant: non-public tracks return 404 to non-owners to prevent enumeration.
/// </summary>
public interface ITrackVisibilityPolicy
{
    /// <summary>
    /// Returns true when the requesting identity may view or stream the track.
    /// </summary>
    bool CanAccess(string visibility, string? creatorId, string? requestingUserId, bool isAdmin);
}
