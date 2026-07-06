using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class CatalogService : ICatalogService
{
    private readonly ITrackRepository _tracks;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ICreatorProfileRepository _profiles;
    private readonly ILogger<CatalogService> _logger;

    public CatalogService(ITrackRepository tracks, UserManager<ApplicationUser> users, ICreatorProfileRepository profiles, ILogger<CatalogService> logger)
    {
        _tracks = tracks;
        _users = users;
        _profiles = profiles;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync(int page = 1, int pageSize = 50, string? genre = null, string? search = null, string? sort = null)
    {
        return await GetCatalogAsync(page, pageSize, genre, search, sort, null, null, null, null);
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync(int page, int pageSize, string? genre, string? search, string? sort,
        string? mood, string? tempo, bool? instrumental, string? duration)
    {
        var tracks = await _tracks.BrowseAsync(page, pageSize, genre, search, sort, mood, tempo, instrumental, duration);
        _logger.LogInformation("Catalog query: page={Page} size={Size} genre={Genre} search={Search} results={Count}", page, pageSize, genre, search, tracks.Count);
        return await MapBatchAsync(tracks);
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync(int page = 1, int pageSize = 20, string? genre = null, string? search = null)
    {
        return await GetCatalogAsync(page, pageSize, genre, search);
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync(int page, int pageSize, string? genre, string? search,
        string? mood, string? tempo, bool? instrumental, string? duration)
    {
        return await GetCatalogAsync(page, pageSize, genre, search, null, mood, tempo, instrumental, duration);
    }

    public async Task<PagedResult<TrackResponse>> GetCatalogPagedAsync(int page, int pageSize, string? genre, string? search, string? sort,
        string? mood, string? tempo, bool? instrumental, string? duration)
    {
        var totalCount = await _tracks.CountAsync(genre, search, mood, tempo, instrumental, duration);
        var tracks = await _tracks.BrowseAsync(page, pageSize, genre, search, sort, mood, tempo, instrumental, duration);
        var items = await MapBatchAsync(tracks);

        return new PagedResult<TrackResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<PagedResult<TrackResponse>> GetDiscoverPagedAsync(int page, int pageSize, string? genre, string? search,
        string? mood, string? tempo, bool? instrumental, string? duration)
    {
        return await GetCatalogPagedAsync(page, pageSize, genre, search, null, mood, tempo, instrumental, duration);
    }

    public async Task<TrackResponse?> GetTrackAsync(string trackId)
    {
        var track = Guid.TryParse(trackId, out var id)
            ? await _tracks.GetByIdAsync(id)
            : await _tracks.GetByCambrianTrackIdAsync(trackId);

        return track is null ? null : await MapToResponseAsync(track);
    }

    /// <summary>
    /// Trending = recent public tracks re-ranked by REAL lifetime plays. We fetch a bounded
    /// window of the newest tracks (so the query stays cheap and uses the existing public
    /// filters), map them — which attaches live play counts — then order by plays.
    /// </summary>
    private const int TrendingWindow = 250;

    public async Task<PagedResult<TrackResponse>> GetTrendingPagedAsync(int page, int pageSize,
        string? genre, string? mood, string? tempo, bool? instrumental, string? duration)
    {
        var candidates = await _tracks.BrowseAsync(1, TrendingWindow, genre, null, "newest",
            mood, tempo, instrumental, duration);
        var mapped = await MapBatchAsync(candidates);

        var ranked = mapped
            .OrderByDescending(t => t.Plays)
            .ThenByDescending(t => t.CreatedAt)
            .ToList();

        var pageItems = ranked
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedResult<TrackResponse>
        {
            Items = pageItems,
            Page = page,
            PageSize = pageSize,
            TotalCount = ranked.Count
        };
    }

    public async Task<PagedResult<TrackResponse>> GetByCreatorPagedAsync(string creatorId, Guid? creatorUuid, int page, int pageSize)
    {
        var all = await _tracks.GetStorefrontTracksAsync(creatorId, creatorUuid);
        var pageSlice = all
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        var items = await MapBatchAsync(pageSlice);

        return new PagedResult<TrackResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        };
    }

    /// <summary>Batch-map a page of tracks — 2 queries total instead of 2×N.</summary>
    private async Task<List<TrackResponse>> MapBatchAsync(List<Track> tracks)
    {
        if (tracks.Count == 0)
            return new List<TrackResponse>();

        var creatorIds = tracks
            .Where(t => !string.IsNullOrEmpty(t.CreatorId))
            .Select(t => t.CreatorId)
            .Distinct()
            .ToList();

        // Single query: load all creators for this page
        var creatorList = _users.Users
            .Where(u => creatorIds.Contains(u.Id))
            .ToList();
        var creators = creatorList.ToDictionary(u => u.Id, u => u);

        // Single query: load all profile slugs/images for this page
        var profileMap = await _profiles.GetSlugsByUserIdsAsync(creatorIds);

        // Two grouped queries: live play/sale counts for every track on this page
        var statsMap = await _tracks.GetTrackStatsAsync(tracks.Select(t => t.Id).ToList())
            ?? new Dictionary<Guid, TrackStats>();

        var result = new List<TrackResponse>(tracks.Count);
        foreach (var t in tracks)
        {
            creators.TryGetValue(t.CreatorId, out var creator);
            profileMap.TryGetValue(t.CreatorId, out var profileInfo);
            statsMap.TryGetValue(t.Id, out var stats);

            var feeRate = creator is not null
                ? TierManifest.For(creator.CreatorTier).FeeRate
                : TierManifest.Free.FeeRate;

            result.Add(BuildTrackResponse(t, feeRate, profileInfo.Slug, profileInfo.ProfileImageUrl, stats));
        }
        return result;
    }

    /// <summary>Map a single track (used by GetTrackAsync — acceptable since it's one item).</summary>
    private async Task<TrackResponse> MapToResponseAsync(Track t)
    {
        decimal feeRate = TierManifest.Free.FeeRate;
        string? creatorSlug = null;
        string? creatorProfileImageUrl = null;
        if (!string.IsNullOrEmpty(t.CreatorId))
        {
            var creator = await _users.FindByIdAsync(t.CreatorId);
            if (creator is not null)
                feeRate = TierManifest.For(creator.CreatorTier).FeeRate;

            var profile = await _profiles.GetByUserIdAsync(t.CreatorId);
            if (profile is not null)
            {
                creatorSlug = profile.Slug;
                creatorProfileImageUrl = profile.ProfileImageUrl;
            }
        }

        var statsMap = await _tracks.GetTrackStatsAsync(new[] { t.Id })
            ?? new Dictionary<Guid, TrackStats>();
        statsMap.TryGetValue(t.Id, out var stats);

        return BuildTrackResponse(t, feeRate, creatorSlug, creatorProfileImageUrl, stats);
    }

    private static TrackResponse BuildTrackResponse(Track t, decimal feeRate, string? creatorSlug, string? creatorProfileImageUrl, TrackStats? stats = null)
    {
        // Fallback: if NonExclusivePriceCents is 0, use legacy Price field (matches checkout logic)
        var legacyPriceDollars = t.Price;
        var nonExPrice = t.NonExclusivePriceCents > 0 ? t.NonExclusivePriceCents / 100m : legacyPriceDollars;

        // Canonical slug: prefer Creator.Username (new identity), fall back to CreatorProfile.Slug
        var canonicalSlug = t.CreatorEntity?.Username ?? creatorSlug;
        var canonicalImage = t.CreatorEntity?.ProfileImageUrl ?? creatorProfileImageUrl;

        return new TrackResponse
        {
            Id = t.Id.ToString(),
            CambrianTrackId = t.CambrianTrackId,
            Title = t.Title,
            Description = t.Description,
            Genre = t.Subgenre ?? t.Genre ?? t.PrimaryGenre ?? "",
            PrimaryGenre = t.PrimaryGenre,
            Subgenre = t.Subgenre,
            Mood = t.Mood,
            Tempo = t.Tempo,
            Tags = t.Tags,
            Instrumental = t.Instrumental,
            Visibility = t.Visibility,
            Price = nonExPrice,
            NonExclusivePrice = nonExPrice,
            Status = t.Status == "exclusive_sold" || t.Status == "copyright_transferred" ? "available" : (t.Status ?? "available"),
            Duration = t.Duration,
            AudioUrl = t.AudioUrl,
            StreamAvailable = !string.IsNullOrWhiteSpace(t.AudioUrl),
            CoverArtUrl = t.CoverArtUrl,
            CreatorId = t.CreatorId,
            CreatorSlug = canonicalSlug,
            CreatorProfileImageUrl = canonicalImage,
            Artist = !string.IsNullOrWhiteSpace(t.CreatorEntity?.DisplayName)
                ? t.CreatorEntity.DisplayName
                : t.CreatorEntity?.Username
                  ?? t.Creator?.DisplayName
                  ?? "Unknown Artist",
            Plays = stats?.Plays ?? 0,
            Sales = stats?.Sales ?? 0,
            AuthorshipRecordId = stats?.AuthorshipRecordId,
            AiGenerated = t.AiGenerated,
            ProvenanceStatus = DeriveProvenanceStatus(t),
            CreatedAt = t.CreatedAt,
        };
    }

    /// <summary>
    /// Derive the public provenance status from the §9 signing fields. Returns a status
    /// string only — the raw <see cref="Track.ContentHash"/> / <see cref="Track.Signature"/>
    /// are never surfaced to public consumers.
    /// </summary>
    private static string DeriveProvenanceStatus(Track t)
    {
        var hasHash = !string.IsNullOrEmpty(t.ContentHash);
        var hasStamp = !string.IsNullOrEmpty(t.Signature) && t.SignedAt is not null;
        if (hasStamp && t.CommercialRightsVerified) return "verified";
        if (hasStamp) return "stamped";
        if (hasHash) return "hashed";
        return "none";
    }
}
