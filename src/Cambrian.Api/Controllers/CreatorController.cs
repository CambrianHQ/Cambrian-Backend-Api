using Cambrian.Api.Common;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.Exceptions;
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
    private readonly ITrackAuthorshipRepository _authorship;
    private readonly IComplianceScoreService _compliance;
    private readonly ILogger<CreatorController> _logger;
    private readonly IMediaReadinessService? _mediaReadiness;

    public CreatorController(
        ICreatorService creator,
        ITrackRepository tracks,
        ICreatorIdentityRepository creators,
        ICreatorProfileRepository profiles,
        IUploadService upload,
        ITrackReadinessCache readinessCache,
        ITrackDetailsRepository trackDetails,
        ITrackAuthorshipRepository authorship,
        IComplianceScoreService compliance,
        ILogger<CreatorController> logger,
        IMediaReadinessService? mediaReadiness = null)
    {
        _creator = creator;
        _tracks = tracks;
        _creators = creators;
        _profiles = profiles;
        _upload = upload;
        _readinessCache = readinessCache;
        _trackDetails = trackDetails;
        _authorship = authorship;
        _compliance = compliance;
        _logger = logger;
        _mediaReadiness = mediaReadiness;
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

    /// <summary>
    /// Owner-scoped track detail: the same shape the mutation endpoints return,
    /// including the free readiness attestations (aiDisclosure,
    /// commercialRightsVerified) and the evaluated releaseReadiness state. This
    /// is how the edit page hydrates saved attestations — the public
    /// GET /tracks/{id} DTO intentionally never carries them.
    /// </summary>
    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpGet("tracks/{trackId:guid}")]
    public async Task<IActionResult> GetOwnTrack(Guid trackId)
    {
        var userId = GetRequiredUserId()!;
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null) return NotFoundResponse("Track not found.");

        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ownsLegacy = track.CreatorId == userId;
        var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
        if (!ownsLegacy && !ownsUuid) return ForbiddenResponse("You can only view your own tracks here.");

        return OkResponse(await BuildMutationResponseAsync(userId, track));
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

            if (visibility == "public")
            {
                // Promote-on-publish: fresh uploads sit in Uploaded until something
                // validates them, so the gate runs validation synchronously and
                // promotes promotable media to Ready instead of deadlocking every
                // new upload behind admin reconciliation. Fails closed (409) only
                // when media is absent, mid-validation, or genuinely invalid.
                var readiness = _mediaReadiness is null
                    ? null
                    : await _mediaReadiness.EnsureReadyAsync(track.Id, HttpContext.RequestAborted);
                if (readiness is null || !readiness.IsReady)
                    return StatusCode(409, new
                    {
                        success = false,
                        error = readiness?.SafeMessage
                            ?? "Track media must be validated and Ready before publishing.",
                        code = readiness?.FailureCode ?? "track_not_ready",
                    });
            }

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

        // Free readiness attestations. These write the exact fields the
        // compliance checklist evaluates (ComplianceScoreService): the
        // checklist's ai_disclosure item reads TrackAuthorship.AiDisclosure
        // and its rights item reads Track.CommercialRightsVerified. Before
        // this, no self-serve surface wrote either — the checklist items were
        // impossible to complete from the edit page.
        if (request.RightsConfirmed.HasValue)
            track.CommercialRightsVerified = request.RightsConfirmed.Value;
        if (request.AiDisclosure is not null)
            await UpsertAiDisclosureAsync(track.Id, request.AiDisclosure);
        if (request.RightsConfirmed.HasValue || request.AiDisclosure is not null)
        {
            _logger.LogInformation(
                "EVENT: readiness_attestation_saved trackId:{TrackId} rightsConfirmed:{Rights} aiDisclosureState:{AiState}",
                track.Id,
                request.RightsConfirmed?.ToString() ?? "unchanged",
                request.AiDisclosure is null ? "unchanged"
                    : string.IsNullOrWhiteSpace(request.AiDisclosure) ? "cleared" : "present");
        }

        await _tracks.UpdateAsync(track);
        _readinessCache.Invalidate(track.Id);
        return OkResponse(await BuildMutationResponseAsync(userId, track));
    }

    /// <summary>
    /// Targeted upsert of the AI-use disclosure on the TrackAuthorship companion
    /// row. Unlike the Creator+ authorship suite upsert, this touches ONLY the
    /// AiDisclosure column so a details-page save can never wipe narrative
    /// authorship fields (edits, arrangement, process notes).
    /// </summary>
    private async Task UpsertAiDisclosureAsync(Guid trackId, string aiDisclosure)
    {
        var normalized = string.IsNullOrWhiteSpace(aiDisclosure) ? null : aiDisclosure.Trim();
        var now = DateTime.UtcNow;
        var row = await _authorship.GetByTrackIdAsync(trackId);

        if (row is null)
        {
            if (normalized is null) return; // nothing stored, nothing to clear
            await _authorship.AddAsync(new Domain.Entities.TrackAuthorship
            {
                Id = Guid.NewGuid(),
                TrackId = trackId,
                AiDisclosure = normalized,
                CreatedAt = now,
                UpdatedAt = now,
            });
            return;
        }

        if (row.AiDisclosure == normalized) return;
        row.AiDisclosure = normalized;
        row.UpdatedAt = now;
        await _authorship.UpdateAsync(row);
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

        var existingLyrics = await _trackDetails.GetLyricsAsync(trackId);
        if (request.DeleteLyrics)
        {
            if (existingLyrics is null)
                return OkResponse<object?>(null, "Lyrics were already absent.");
            if (!request.Version.HasValue)
                return ConflictResponse("Reload the latest lyrics before deleting them.");

            try
            {
                await _trackDetails.DeleteLyricsAsync(trackId, request.Version.Value);
                _logger.LogInformation(
                    "Track lyrics operation completed TrackId:{TrackId} CreatorId:{CreatorId} Operation:delete Result:success",
                    trackId, userId);
                return OkResponse<object?>(null, "Lyrics removed.");
            }
            catch (TrackLyricsConcurrencyException)
            {
                _logger.LogWarning(
                    "Track lyrics operation rejected TrackId:{TrackId} CreatorId:{CreatorId} Operation:delete ErrorCode:lyrics_version_conflict",
                    trackId, userId);
                return ConflictResponse("These lyrics changed in another session. Reload before deleting them.");
            }
        }

        var lyrics = request.Lyrics ?? "";
        if (string.IsNullOrWhiteSpace(lyrics))
            return ErrorResponse("Lyrics cannot be empty. Use the explicit delete action to remove them.");
        if (existingLyrics is not null && !request.Version.HasValue)
            return ConflictResponse("Reload the latest lyrics before saving changes.");

        var language = NormalizeLanguageTag(request.Language);
        if (language is null)
            return ErrorResponse("Language must be a valid language tag (e.g. 'en', 'pt-BR').");

        try
        {
            var saved = await _trackDetails.UpsertLyricsAsync(
                trackId, lyrics, language, request.IsExplicit, request.Version);
            _logger.LogInformation(
                "Track lyrics operation completed TrackId:{TrackId} CreatorId:{CreatorId} Operation:upsert Result:success Version:{Version}",
                trackId, userId, saved.Version);
            return OkResponse(saved);
        }
        catch (TrackLyricsConcurrencyException)
        {
            _logger.LogWarning(
                "Track lyrics operation rejected TrackId:{TrackId} CreatorId:{CreatorId} Operation:upsert ErrorCode:lyrics_version_conflict",
                trackId, userId);
            return ConflictResponse("These lyrics changed in another session. Reload and review the latest version before saving.");
        }
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

        // Saves return the canonical persisted attestations plus the freshly
        // evaluated readiness state, so the checklist UI can never show a stale
        // "incomplete" after a successful save. The evaluator is the same
        // ComplianceScoreService that GET /api/tracks/{id}/compliance-score uses —
        // one authority, two surfaces.
        var authorship = await _authorship.GetByTrackIdAsync(track.Id);
        var releaseReadiness = await _compliance.ComputeAsync(track);

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
            collectionTitle = linkedCollection?.Title,
            aiDisclosure = authorship?.AiDisclosure,
            commercialRightsVerified = track.CommercialRightsVerified,
            releaseReadiness
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
