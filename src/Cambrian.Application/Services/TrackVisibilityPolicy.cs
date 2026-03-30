using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

/// <summary>
/// Single source of truth for track read/stream access.
/// Rule: non-public tracks are only accessible by their creator or an admin.
/// Returns false (caller emits 404) to avoid leaking that the track exists.
/// </summary>
public sealed class TrackVisibilityPolicy : ITrackVisibilityPolicy
{
    /// <inheritdoc />
    public bool CanAccess(string visibility, string? creatorId, string? requestingUserId, bool isAdmin)
    {
        if (visibility == "public") return true;
        if (isAdmin) return true;
        return !string.IsNullOrEmpty(requestingUserId)
            && string.Equals(creatorId, requestingUserId, StringComparison.Ordinal);
    }
}
