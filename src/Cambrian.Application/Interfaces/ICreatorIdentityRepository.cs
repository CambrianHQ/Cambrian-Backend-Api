using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.Creators;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Repository for the first-class Creators table.
/// All queries use creator UUID — never email or username as relational keys.
/// </summary>
public interface ICreatorIdentityRepository
{
    /// <summary>Load creator by UUID primary key.</summary>
    Task<PublicCreatorDto?> GetByIdAsync(Guid creatorId);

    /// <summary>Resolve normalized username to creator, then return public DTO.</summary>
    Task<PublicCreatorDto?> GetByUsernameAsync(string username);

    /// <summary>Load creator by the linked ApplicationUser.Id.</summary>
    Task<PublicCreatorDto?> GetByUserIdAsync(string userId);

    /// <summary>
    /// Get tracks filtered strictly by creatorId (UUID FK).
    /// Joins Creator once to populate creatorUsername and creatorDisplayName.
    /// Must NOT filter by email or username.
    /// </summary>
    Task<List<TrackResponse>> GetTracksByCreatorIdAsync(Guid creatorId, int page, int pageSize);

    /// <summary>Check if a normalized username is already taken.</summary>
    Task<bool> IsUsernameTakenAsync(string normalizedUsername, Guid? excludeCreatorId = null);

    /// <summary>Create or update the creator profile for the authenticated user.</summary>
    Task<PublicCreatorDto> UpsertAsync(string userId, UpdateCreatorProfileRequest request);

    /// <summary>Get stats (track count, total sales, downloads) for a creator.</summary>
    Task<CreatorStatsResponseDto> GetStatsAsync(Guid creatorId);

    /// <summary>Get the Creator entity ID for a given ApplicationUser ID, or null.</summary>
    Task<Guid?> GetCreatorIdForUserAsync(string userId);

    /// <summary>
    /// Compatibility resolver: accept a legacy identifier (ApplicationUser.Id string or UUID string)
    /// and return the canonical PublicCreatorDto. Returns null if unresolvable.
    /// </summary>
    Task<PublicCreatorDto?> ResolveByLegacyIdentifierAsync(string identifier);
}
