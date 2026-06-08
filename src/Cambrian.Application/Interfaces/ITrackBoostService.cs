using System.Security.Claims;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.Community;

namespace Cambrian.Application.Interfaces;

public interface ITrackBoostService
{
    /// <summary>
    /// "Hot This Week" — public tracks ranked by boosts in the rolling 7-day
    /// window, highest first, paginated. HasBoosted reflects the current user.
    /// </summary>
    Task<PagedResult<HotTrackResponse>> GetHotThisWeekAsync(ClaimsPrincipal user, int page, int pageSize);

    /// <summary>Boost (upvote) a public track. Rejects self-boosts. One per user (idempotent).</summary>
    Task<BoostStatusResponse> BoostAsync(ClaimsPrincipal user, string trackId);

    /// <summary>Remove the current user's boost from a track (idempotent).</summary>
    Task<BoostStatusResponse> UnboostAsync(ClaimsPrincipal user, string trackId);

    /// <summary>Current boost count for a track plus whether the caller has boosted it.</summary>
    Task<BoostStatusResponse> GetStatusAsync(ClaimsPrincipal user, string trackId);
}
