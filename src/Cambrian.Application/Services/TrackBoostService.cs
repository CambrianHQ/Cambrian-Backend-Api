using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.Community;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <summary>
/// Boosts (upvote-only) for the creator-community layer. Enforces the boost
/// rules: one boost per user per track (DB UNIQUE constraint + idempotent
/// insert), no self-boosts, public tracks only. Email-verification is enforced
/// by the [VerifiedEmail] policy on the controller.
/// </summary>
public class TrackBoostService : ITrackBoostService
{
    private readonly ITrackBoostRepository _boosts;
    private readonly ITrackRepository _tracks;
    private readonly ILogger<TrackBoostService> _logger;

    public TrackBoostService(ITrackBoostRepository boosts, ITrackRepository tracks, ILogger<TrackBoostService> logger)
    {
        _boosts = boosts;
        _tracks = tracks;
        _logger = logger;
    }

    public async Task<BoostStatusResponse> BoostAsync(ClaimsPrincipal user, string trackId)
    {
        var userId = GetRequiredUserId(user);
        var id = ParseTrackId(trackId);
        var track = await LoadPublicTrackAsync(id);

        // A creator cannot boost their own track (enforced server-side).
        if (string.Equals(track.CreatorId, userId, StringComparison.Ordinal))
            throw new InvalidOperationException("You cannot boost your own track.");

        if (await _boosts.GetByUserAndTrackAsync(userId, id) is null)
        {
            await _boosts.AddAsync(new TrackBoost { UserId = userId, TrackId = id });
            _logger.LogInformation("EVENT: TrackBoosted userId:{UserId} trackId:{TrackId}", userId, id);
        }

        return await BuildStatusAsync(id, userId, hasBoosted: true);
    }

    public async Task<BoostStatusResponse> UnboostAsync(ClaimsPrincipal user, string trackId)
    {
        var userId = GetRequiredUserId(user);
        var id = ParseTrackId(trackId);
        _ = await LoadPublicTrackAsync(id);

        var existing = await _boosts.GetByUserAndTrackAsync(userId, id);
        if (existing is not null)
        {
            await _boosts.RemoveAsync(existing.Id);
            _logger.LogInformation("EVENT: TrackUnboosted userId:{UserId} trackId:{TrackId}", userId, id);
        }

        return await BuildStatusAsync(id, userId, hasBoosted: false);
    }

    private const int HotWindowDays = 7;

    public async Task<PagedResult<HotTrackResponse>> GetHotThisWeekAsync(ClaimsPrincipal user, int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;

        // Rolling window — keeps the chart fresh and lets newcomers win.
        var since = DateTime.UtcNow.AddDays(-HotWindowDays);
        var skip = (page - 1) * pageSize;

        var ranked = await _boosts.GetHotSinceAsync(since, skip, pageSize);
        var total = await _boosts.CountHotSinceAsync(since);

        var userId = TryGetUserId(user);
        var boostedIds = userId is null
            ? new HashSet<Guid>()
            : (await _boosts.GetBoostedTrackIdsAsync(userId, ranked.Select(r => r.Track.Id).ToList())).ToHashSet();

        var items = ranked.Select((r, i) => new HotTrackResponse
        {
            Rank = skip + i + 1,
            BoostCount = r.BoostCount,
            HasBoosted = boostedIds.Contains(r.Track.Id),
            Id = r.Track.Id.ToString(),
            Title = r.Track.Title,
            Genre = r.Track.Subgenre ?? r.Track.Genre ?? r.Track.PrimaryGenre,
            CoverArtUrl = r.Track.CoverArtUrl,
            AudioUrl = r.Track.AudioUrl,
            CreatorId = r.Track.CreatorId,
            CreatorName = r.Track.Creator?.DisplayName ?? r.Track.Creator?.UserName,
            CreatorSlug = r.Track.CreatorEntity?.Username,
        }).ToList();

        return new PagedResult<HotTrackResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
        };
    }

    public async Task<BoostStatusResponse> GetStatusAsync(ClaimsPrincipal user, string trackId)
    {
        var id = ParseTrackId(trackId);
        _ = await LoadPublicTrackAsync(id);

        var userId = TryGetUserId(user);
        var hasBoosted = userId is not null
            && await _boosts.GetByUserAndTrackAsync(userId, id) is not null;

        return await BuildStatusAsync(id, userId, hasBoosted);
    }

    private async Task<BoostStatusResponse> BuildStatusAsync(Guid trackId, string? userId, bool hasBoosted)
    {
        var count = await _boosts.CountByTrackAsync(trackId);
        return new BoostStatusResponse
        {
            TrackId = trackId.ToString(),
            BoostCount = count,
            HasBoosted = userId is not null && hasBoosted,
        };
    }

    private async Task<Track> LoadPublicTrackAsync(Guid trackId)
    {
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null || !string.Equals(track.Visibility, "public", StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Track {trackId} not found.");
        return track;
    }

    private static Guid ParseTrackId(string trackId) =>
        Guid.TryParse(trackId, out var id)
            ? id
            : throw new FormatException("Track id must be a GUID.");

    private static string GetRequiredUserId(ClaimsPrincipal user) =>
        TryGetUserId(user) ?? throw new UnauthorizedAccessException("No user identity found.");

    private static string? TryGetUserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);
}
