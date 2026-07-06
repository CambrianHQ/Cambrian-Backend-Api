using Cambrian.Api.Common;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("creator")]
[Authorize]
[RequireCreatorTier]
[RequireUsername]
[EnableRateLimiting("auth")]
public class CreatorController : BaseController
{
    private readonly ICreatorService _creator;
    private readonly ITrackRepository _tracks;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ICreatorProfileRepository _profiles;
    private readonly IUploadService _upload;
    private readonly ITrackReadinessCache _readinessCache;
    private readonly ITrackDetailsRepository _trackDetails;
    private readonly ILogger<CreatorController> _logger;

    public CreatorController(
        ICreatorService creator,
        ITrackRepository tracks,
        ICreatorIdentityRepository creators,
        ICreatorProfileRepository profiles,
        IUploadService upload,
        ITrackReadinessCache readinessCache,
        ITrackDetailsRepository trackDetails,
        ILogger<CreatorController> logger)
    {
        _creator = creator;
        _tracks = tracks;
        _creators = creators;
        _profiles = profiles;
        _upload = upload;
        _readinessCache = readinessCache;
        _trackDetails = trackDetails;
        _logger = logger;
    }

    [HttpGet("tracks")]
    public async Task<IActionResult> Tracks([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;

        var userId = GetRequiredUserId()!;
        var paged = await _creator.GetTracksAsync(userId, page, pageSize);
        foreach (var t in paged.Items)
        {
            t.AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio");
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveImageUrl(t.CoverArtUrl);
        }
        return Ok(new CatalogPageResponse
        {
            Data = paged.Items,
            Page = paged.Page,
            PageSize = paged.PageSize,
            TotalCount = paged.TotalCount,
            TotalPages = paged.TotalPages,
            HasNextPage = paged.HasNextPage,
            HasPreviousPage = paged.HasPreviousPage,
        });
    }

    [HttpGet("revenue")]
    public async Task<IActionResult> Revenue()
    {
        var userId = GetRequiredUserId()!;
        var revenue = await _creator.GetRevenueAsync(userId);
        return OkResponse(revenue);
    }

    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpPut("tracks/{trackId:guid}")]
    public async Task<IActionResult> EditTrack(Guid trackId, [FromBody] EditTrackRequest request)
    {
        var userId = GetRequiredUserId()!;
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null) return NotFoundResponse("Track not found.");

        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ownsLegacy = track.CreatorId == userId;
        var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
        if (!ownsLegacy && !ownsUuid) return ForbiddenResponse("You can only edit your own tracks.");

        if (request.Title is not null)
            track.Title = MetadataSanitizer.NormalizeRequired(request.Title, "Track title");
        if (request.Description is not null)
            track.Description = MetadataSanitizer.NormalizeOptional(request.Description, "Track description");
        if (request.PrimaryGenre is not null || request.Subgenre is not null || request.Genre is not null)
            ApplyGenreUpdates(track, request.PrimaryGenre, request.Subgenre, request.Genre);
        if (request.Mood is not null)
            track.Mood = MetadataSanitizer.NormalizeOptional(request.Mood, "Mood");
        if (request.Tempo is not null)
            track.Tempo = MetadataSanitizer.NormalizeOptional(request.Tempo, "Tempo");
        if (request.Tags is not null)
        {
            var tags = MetadataSanitizer.NormalizeOptional(request.Tags, "Tags");
            track.Tags = tags?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
                ?? new List<string>();
        }
        if (request.NonExclusivePriceCents.HasValue)
        {
            track.NonExclusivePriceCents = request.NonExclusivePriceCents.Value;
            track.Price = request.NonExclusivePriceCents.Value / 100m;
        }
        if (request.ExclusivePriceCents.HasValue) track.ExclusivePriceCents = request.ExclusivePriceCents.Value;
        if (request.CopyrightBuyoutPriceCents.HasValue) track.CopyrightBuyoutPriceCents = request.CopyrightBuyoutPriceCents.Value;
        if (request.Visibility is not null)
        {
            // Publish / unpublish (bulk-upload drafts). Same in-place partial
            // update as every other field — id, engagement, and URLs untouched.
            var visibility = request.Visibility.Trim().ToLowerInvariant();
            if (visibility is not ("public" or "hidden"))
                return ErrorResponse("Visibility must be 'public' or 'hidden'.");

            // Hiding a track that fans already paid for would revoke their
            // streaming access (the visibility policy has no purchaser
            // carve-out) while downloads kept working. Refuse rather than
            // silently break paying supporters' playback.
            if (visibility == "hidden" && track.Visibility != "hidden")
            {
                var stats = await _tracks.GetTrackStatsAsync(new[] { track.Id });
                if (stats.TryGetValue(track.Id, out var trackStats) && trackStats.Sales > 0)
                    return ErrorResponse("This track can't be unpublished — fans have already purchased it.");
            }

            track.Visibility = visibility;
        }

        await _tracks.UpdateAsync(track);
        _readinessCache.Invalidate(track.Id);
        return OkResponse(await BuildMutationResponseAsync(userId, track));
    }

    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpPut("tracks/{trackId:guid}/cover-art")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UpdateTrackCoverArt(Guid trackId, [FromForm] UpdateTrackCoverArtRequest request)
    {
        var userId = GetRequiredUserId()!;
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null) return NotFoundResponse("Track not found.");

        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ownsLegacy = track.CreatorId == userId;
        var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
        if (!ownsLegacy && !ownsUuid) return ForbiddenResponse("You can only edit your own tracks.");

        if (request.CoverArt is null || request.CoverArt.Length <= 0)
            return ErrorResponse("Cover art file is required.");

        track.CoverArtUrl = await _upload.UploadCoverArtAsync(userId, request.CoverArt);
        await _tracks.UpdateAsync(track);
        _readinessCache.Invalidate(track.Id);

        return OkResponse(await BuildMutationResponseAsync(userId, track));
    }

    // ───── Lyrics (1:1 companion row — never touches the Track row) ─────

    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpPut("tracks/{trackId:guid}/lyrics")]
    public async Task<IActionResult> UpsertTrackLyrics(Guid trackId, [FromBody] UpsertTrackLyricsRequest request)
    {
        var userId = GetRequiredUserId()!;
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null) return NotFoundResponse("Track not found.");

        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ownsLegacy = track.CreatorId == userId;
        var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
        if (!ownsLegacy && !ownsUuid) return ForbiddenResponse("You can only edit your own tracks.");

        var lyrics = request.Lyrics?.Trim() ?? "";
        if (lyrics.Length == 0)
        {
            await _trackDetails.DeleteLyricsAsync(trackId);
            return OkResponse<object?>(null, "Lyrics removed.");
        }

        var language = NormalizeLanguageTag(request.Language);
        if (language is null)
            return ErrorResponse("Language must be a valid language tag (e.g. 'en', 'pt-BR').");

        var saved = await _trackDetails.UpsertLyricsAsync(trackId, lyrics, language);
        return OkResponse(saved);
    }

    // ───── Behind The Track (1:1 companion row — never touches the Track row) ─────

    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpPut("tracks/{trackId:guid}/behind-the-track")]
    public async Task<IActionResult> UpsertBehindTheTrack(Guid trackId, [FromBody] UpsertBehindTheTrackRequest request)
    {
        var userId = GetRequiredUserId()!;
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null) return NotFoundResponse("Track not found.");

        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ownsLegacy = track.CreatorId == userId;
        var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
        if (!ownsLegacy && !ownsUuid) return ForbiddenResponse("You can only edit your own tracks.");

        var story = MetadataSanitizer.NormalizeOptional(request.Story, "Story");
        var daw = MetadataSanitizer.NormalizeOptional(request.DAW, "DAW");
        var vocalChain = MetadataSanitizer.NormalizeOptional(request.VocalChain, "Vocal chain");
        var promptNotes = MetadataSanitizer.NormalizeOptional(request.PromptNotes, "Prompt notes");
        var productionNotes = MetadataSanitizer.NormalizeOptional(request.ProductionNotes, "Production notes");
        var humanContributionNotes = MetadataSanitizer.NormalizeOptional(request.HumanContributionNotes, "Human contribution notes");
        var youtubeUrl = request.YoutubeUrl?.Trim();
        if (!string.IsNullOrEmpty(youtubeUrl) && !IsYoutubeUrl(youtubeUrl))
            return ErrorResponse("Process video must be a YouTube URL (youtube.com or youtu.be).");
        youtubeUrl = string.IsNullOrEmpty(youtubeUrl) ? null : youtubeUrl;

        var tools = (request.ToolsUsed ?? new List<string>())
            .Select(t => t?.Trim() ?? "")
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
        if (tools.Any(t => t.Length > 100))
            return ErrorResponse("Each tool name must be 100 characters or less.");
        // The column stores the JSON-serialized list (varchar 2000). 30×100-char
        // tools can serialize past that, which would 500 at SaveChanges on
        // Postgres — reject it here with an actionable message instead.
        if (tools.Count > 0 && System.Text.Json.JsonSerializer.Serialize(tools).Length > 2000)
            return ErrorResponse("Tools list is too long — remove some tools or shorten their names.");

        if (story is null && daw is null && vocalChain is null && promptNotes is null
            && productionNotes is null && humanContributionNotes is null
            && youtubeUrl is null && tools.Count == 0)
        {
            await _trackDetails.DeleteCreationProcessAsync(trackId);
            return OkResponse<object?>(null, "Behind The Track removed.");
        }

        var saved = await _trackDetails.UpsertCreationProcessAsync(
            trackId, story, daw, vocalChain, promptNotes, productionNotes, humanContributionNotes, youtubeUrl, tools);
        return OkResponse(saved);
    }

    private static readonly HashSet<string> YoutubeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be"
    };

    private static bool IsYoutubeUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == "https" || uri.Scheme == "http")
            && string.IsNullOrEmpty(uri.UserInfo)
            && YoutubeHosts.Contains(uri.Host);
    }

    /// <summary>Accepts short BCP-47-ish tags: "en", "pt-BR", "zh-Hans". Null/empty → "en".</summary>
    private static string? NormalizeLanguageTag(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return "en";
        if (value.Length > 16) return null;
        return value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? value : null;
    }

    [Authorize(Policy = "CanDeleteOwnTrack")]
    [HttpDelete("tracks/{trackId:guid}")]
    public async Task<IActionResult> DeleteTrack(Guid trackId)
    {
        var userId = GetRequiredUserId()!;
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null) return NotFoundResponse("Track not found.");

        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ownsLegacy = track.CreatorId == userId;
        var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
        if (!ownsLegacy && !ownsUuid) return ForbiddenResponse("You can only delete your own tracks.");

        try
        {
            await RemoveTrackFromCollectionsAsync(userId, track.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Creator track delete collection cleanup failed. UserId={UserId} TrackId={TrackId}",
                userId,
                track.Id);
        }

        await _tracks.DeleteAsync(track.Id);

        return OkResponse(new
        {
            deleted = true,
            trackId = track.Id,
            cambrianTrackId = track.CambrianTrackId
        }, "Track deleted successfully.");
    }

    private async Task<object> BuildMutationResponseAsync(string userId, Domain.Entities.Track track)
    {
        var linkedCollection = await FindLinkedCollectionAsync(userId, track.Id);
        var pricing = TrackPricingSnapshot.FromTrack(track);

        return new
        {
            id = track.Id,
            cambrianTrackId = track.CambrianTrackId,
            title = track.Title,
            description = track.Description,
            genre = track.Subgenre ?? track.Genre ?? track.PrimaryGenre,
            primaryGenre = track.PrimaryGenre,
            subgenre = track.Subgenre,
            mood = track.Mood,
            tempo = track.Tempo,
            tags = track.Tags,
            coverArtUrl = string.IsNullOrWhiteSpace(track.CoverArtUrl) ? null : ResolveImageUrl(track.CoverArtUrl),
            price = pricing.Price,
            nonExclusivePrice = pricing.NonExclusivePrice,
            exclusivePrice = pricing.ExclusivePrice,
            copyrightBuyoutPrice = pricing.CopyrightBuyoutPrice,
            nonExclusivePriceCents = pricing.NonExclusivePriceCents,
            exclusivePriceCents = pricing.ExclusivePriceCents,
            copyrightBuyoutPriceCents = pricing.CopyrightBuyoutPriceCents,
            collectionId = linkedCollection?.Id,
            collectionTitle = linkedCollection?.Title
        };
    }

    private async Task<TrackCollectionDto?> FindLinkedCollectionAsync(string userId, Guid trackId)
    {
        var trackIdString = trackId.ToString();
        var collections = await _profiles.GetCollectionsAsync(userId);
        return collections.FirstOrDefault(c => c.TrackIds.Contains(trackIdString, StringComparer.OrdinalIgnoreCase));
    }

    private async Task RemoveTrackFromCollectionsAsync(string userId, Guid trackId)
    {
        var trackIdString = trackId.ToString();
        var collections = await _profiles.GetCollectionsAsync(userId);
        foreach (var collection in collections.Where(c => c.TrackIds.Contains(trackIdString, StringComparer.OrdinalIgnoreCase)))
        {
            var updatedTrackIds = string.Join(",",
                collection.TrackIds
                    .Where(id => !string.Equals(id, trackIdString, StringComparison.OrdinalIgnoreCase)));

            await _profiles.UpdateCollectionAsync(
                Guid.Parse(collection.Id),
                userId,
                null,
                null,
                null,
                updatedTrackIds);
        }
    }

    private static void ApplyGenreUpdates(Domain.Entities.Track track, string? primaryGenre, string? subgenre, string? legacyGenre)
    {
        var normalizedPrimary = NormalizeNullableText(primaryGenre);
        var normalizedSubgenre = NormalizeNullableText(subgenre);
        var normalizedLegacy = NormalizeNullableText(legacyGenre);

        if (primaryGenre is not null)
            track.PrimaryGenre = normalizedPrimary;

        if (subgenre is not null)
            track.Subgenre = normalizedSubgenre;
        else if (legacyGenre is not null)
            track.Subgenre = normalizedLegacy;

        track.Genre = track.Subgenre ?? track.PrimaryGenre ?? normalizedLegacy;
    }

private static string? NormalizeNullableText(string? value) =>
    MetadataSanitizer.NormalizeOptional(value, "Genre metadata");
}
