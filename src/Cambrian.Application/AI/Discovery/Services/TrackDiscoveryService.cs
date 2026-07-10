using Cambrian.Application.AI.Discovery.Builders;
using Cambrian.Application.AI.Discovery.Dtos;
using Cambrian.Application.AI.Discovery.Queries;
using Cambrian.Application.AI.Discovery.Ranking;
using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.AI.Discovery.Services;

public class TrackDiscoveryService : ITrackDiscoveryService
{
    private readonly ITrackRepository _tracks;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ICreatorProfileRepository _profiles;
    private readonly ITrackRankingService _ranking;
    private readonly IObjectStorage _storage;
    private readonly ILogger<TrackDiscoveryService> _logger;

    public TrackDiscoveryService(
        ITrackRepository tracks,
        UserManager<ApplicationUser> users,
        ICreatorIdentityRepository creators,
        ICreatorProfileRepository profiles,
        ITrackRankingService ranking,
        IObjectStorage storage,
        ILogger<TrackDiscoveryService> logger)
    {
        _tracks = tracks;
        _users = users;
        _creators = creators;
        _profiles = profiles;
        _ranking = ranking;
        _storage = storage;
        _logger = logger;
    }

    public async Task<AiTrackSearchResponse> SearchAsync(SearchTracksQuery query)
    {
        // Map new query fields to existing repository filter params
        bool? instrumental = query.InstrumentalOnly ? true : null;

        var tracks = await _tracks.BrowseAsync(
            query.Page, query.PageSize,
            query.Genre, query.Query, "trending",
            query.Mood, null /* tempo */, instrumental, null /* duration */);

        var totalCount = await _tracks.CountAsync(
            query.Genre, query.Query,
            query.Mood, null, instrumental, null);

        if (tracks.Count == 0)
            return new AiTrackSearchResponse
            {
                Results = new List<AiTrackSearchResult>(),
                Page = query.Page,
                PageSize = query.PageSize,
                TotalCount = totalCount,
                QuerySummary = TrackAiResponseBuilder.BuildQuerySummary(query, 0)
            };

        // Batch-load creator data (N+1 prevention)
        var creatorIds = tracks.Select(t => t.CreatorId).Distinct().ToList();
        var creatorMap = _users.Users
            .Where(u => creatorIds.Contains(u.Id))
            .ToDictionary(u => u.Id);

        // Score, rank, and build AI responses
        var results = new List<AiTrackSearchResult>(tracks.Count);
        foreach (var track in tracks)
        {
            creatorMap.TryGetValue(track.CreatorId, out var creator);

            // Apply duration filter
            if (query.MinDurationSeconds.HasValue || query.MaxDurationSeconds.HasValue)
            {
                var durationSec = TrackAiResponseBuilder.ParseDurationSeconds(track.Duration);
                if (query.MinDurationSeconds.HasValue && durationSec < query.MinDurationSeconds.Value) continue;
                if (query.MaxDurationSeconds.HasValue && durationSec > query.MaxDurationSeconds.Value) continue;
            }

            // Apply vocals filter
            if (!query.VocalsAllowed && !track.Instrumental) continue;

            var score = _ranking.ComputeScore(track, query);
            var result = TrackAiResponseBuilder.Build(track, score, query, creator);
            results.Add(result);
        }

        // Re-sort by score descending
        results.Sort((a, b) => b.Score.CompareTo(a.Score));

        return new AiTrackSearchResponse
        {
            Results = results,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = totalCount,
            QuerySummary = TrackAiResponseBuilder.BuildQuerySummary(query, results.Count)
        };
    }

    public async Task<AiTrackDetails?> GetTrackDetailsAsync(string trackId)
    {
        var track = await ResolveTrackAsync(trackId);
        if (track is null) return null;

        var creator = await _users.FindByIdAsync(track.CreatorId);

        var details = TrackAiResponseBuilder.BuildDetails(track, creator);
        // Single-track read: replace the optimistic key-presence flag with a real existence
        // check so a track whose object is missing (the rehydration gap) reports unplayable.
        details.Preview.Available = await StreamObjectExistsAsync(track);
        return details;
    }

    public async Task<AiTrackPreview?> GetPreviewAsync(string trackId)
    {
        var track = await ResolveTrackAsync(trackId);
        if (track is null) return null;

        var preview = TrackAiResponseBuilder.BuildPreview(track);
        preview.Available = await StreamObjectExistsAsync(track);
        return preview;
    }

    /// <summary>
    /// True only when the track's audio key resolves to a real stored object — the same
    /// "playable iff object exists" contract the stream endpoint enforces. Bounded to a single
    /// probe; used only on the single-track detail/preview reads, not on search results.
    /// </summary>
    private async Task<bool> StreamObjectExistsAsync(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.AudioUrl)) return false;
        using var file = await _storage.OpenReadAsync(track.AudioUrl);
        return file is not null;
    }

    public async Task<AiCreatorProfile?> GetCreatorProfileAsync(string creatorId)
    {
        // Try by username first, then by userId
        var dto = await _creators.GetByUsernameAsync(creatorId);
        dto ??= await _creators.ResolveByLegacyIdentifierAsync(creatorId);
        if (dto is null) return null;

        var profile = await _profiles.GetByUserIdAsync(dto.UserId);
        var user = await _users.FindByIdAsync(dto.UserId);

        // Get creator's tracks for highlights and featured genres/moods
        var creatorTracks = await _tracks.GetStorefrontTracksAsync(dto.UserId, Guid.Parse(dto.Id));

        var featuredGenres = creatorTracks
            .Where(t => !string.IsNullOrEmpty(t.Genre))
            .Select(t => t.Genre!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        var featuredMoods = creatorTracks
            .Where(t => !string.IsNullOrEmpty(t.Mood))
            .Select(t => t.Mood!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        var highlights = creatorTracks
            .Take(5)
            .Select(t => new AiCreatorCatalogHighlight
            {
                TrackId = t.CambrianTrackId,
                Title = t.Title,
                BestUseCase = t.UseCase ?? "general",
                PreviewAvailable = !string.IsNullOrEmpty(t.AudioUrl)
            })
            .ToList();

        return new AiCreatorProfile
        {
            CreatorId = dto.UserId,
            DisplayName = dto.DisplayName ?? dto.Username ?? "Unknown Artist",
            Bio = profile?.Bio ?? dto.Bio,
            VerifiedCreator = user?.VerifiedCreator ?? false,
            AvatarUrl = dto.ProfileImageUrl,
            ProfileUrl = dto.Username is not null ? $"/creators/{dto.Username}" : null,
            TrackCount = dto.Stats.TrackCount,
            FeaturedGenres = featuredGenres,
            FeaturedMoods = featuredMoods,
            CatalogHighlights = highlights
        };
    }

    public async Task<List<AiLicenseOption>?> GetLicenseOptionsAsync(string trackId)
    {
        var track = await ResolveTrackAsync(trackId);
        if (track is null) return null;

        return TrackAiResponseBuilder.BuildLicenseOptions(track);
    }

    private async Task<Track?> ResolveTrackAsync(string trackId)
    {
        // Try CambrianTrackId first, then Guid
        var track = await _tracks.GetByCambrianTrackIdAsync(trackId);
        if (track is null && Guid.TryParse(trackId, out var guid))
            track = await _tracks.GetByIdAsync(guid);

        // AI discovery is anonymous. Only expose public tracks — mirror SearchAsync's
        // public-only filter so hidden/limited tracks can't be enumerated by id.
        if (track is null || !string.Equals(track.Visibility, "public", StringComparison.OrdinalIgnoreCase))
            return null;

        return track;
    }
}
