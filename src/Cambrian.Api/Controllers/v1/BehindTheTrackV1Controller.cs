using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Cambrian.Api.Middleware;
using Cambrian.Api.Security;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.V1;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Validation;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers.v1;

/// <summary>
/// Public versioned API — "Behind The Track" creation-process notes and proof videos.
/// GET is public (subject to the shared track visibility policy and per-video visibility);
/// all mutations require the owning creator's JWT (CanEditOwnTrack — never an API key).
/// </summary>
[ApiController]
[Route("api/v1/tracks/{trackId}")]
[EnableRateLimiting("api_key_free")]
[ServiceFilter(typeof(ApiUsageActionFilter))]
public class BehindTheTrackV1Controller : ControllerBase
{
    private readonly ITrackRepository _tracks;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ITrackDetailsRepository _trackDetails;
    private readonly ITrackVisibilityPolicy _visibility;

    public BehindTheTrackV1Controller(
        ITrackRepository tracks,
        ICreatorIdentityRepository creators,
        ITrackDetailsRepository trackDetails,
        ITrackVisibilityPolicy visibility)
    {
        _tracks = tracks;
        _creators = creators;
        _trackDetails = trackDetails;
        _visibility = visibility;
    }

    // ───────────────────────── GET (public, visibility-aware) ─────────────────────────

    [HttpGet("behind-the-track")]
    [AllowAnonymous]
    [AllowApiKey]
    public async Task<IActionResult> GetBehindTheTrack(string trackId)
    {
        var track = await ResolveTrackAsync(trackId);
        if (track is null) return NotFound(V1ApiResponse<object>.Fail("Track not found."));

        var requesterId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var isAdmin = User?.IsInRole("Admin") == true;
        if (!_visibility.CanAccess(track.Visibility ?? "public", track.CreatorId, requesterId, isAdmin))
            return NotFound(V1ApiResponse<object>.Fail("Track not found."));

        var isOwner = await IsOwnerAsync(track, requesterId);

        var process = await _trackDetails.GetCreationProcessAsync(track.Id);
        var videos = await _trackDetails.GetProofVideosAsync(track.Id, includeHidden: isOwner || isAdmin);

        var payload = new BehindTheTrackDto
        {
            TrackId = track.Id.ToString(),
            Story = process?.Story,
            DAW = process?.DAW,
            VocalChain = process?.VocalChain,
            PromptNotes = process?.PromptNotes,
            ProductionNotes = process?.ProductionNotes,
            HumanContributionNotes = process?.HumanContributionNotes,
            YoutubeUrl = process?.YoutubeUrl,
            ToolsUsed = process?.ToolsUsed ?? Array.Empty<string>(),
            CreatedAt = process?.CreatedAt ?? default,
            UpdatedAt = process?.UpdatedAt ?? default,
            ProofVideos = videos,
        };

        return Ok(V1ApiResponse<object>.Ok((object)payload));
    }

    // ───────────────────────── PUT (owner-only) ─────────────────────────

    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpPut("behind-the-track")]
    public async Task<IActionResult> UpsertBehindTheTrack(string trackId, [FromBody] UpsertBehindTheTrackRequest request)
    {
        var userId = GetRequiredUserId();
        var track = await ResolveTrackAsync(trackId);
        if (track is null) return NotFound(V1ApiResponse<object>.Fail("Track not found."));
        if (!await IsOwnerAsync(track, userId))
            return StatusCode(403, V1ApiResponse<object>.Fail("You can only edit your own tracks."));

        string? story, daw, vocalChain, promptNotes, productionNotes, humanContributionNotes;
        try
        {
            story = MetadataSanitizer.NormalizeOptional(request.Story, "Story");
            daw = MetadataSanitizer.NormalizeOptional(request.DAW, "DAW");
            vocalChain = MetadataSanitizer.NormalizeOptional(request.VocalChain, "Vocal chain");
            promptNotes = MetadataSanitizer.NormalizeOptional(request.PromptNotes, "Prompt notes");
            productionNotes = MetadataSanitizer.NormalizeOptional(request.ProductionNotes, "Production notes");
            humanContributionNotes = MetadataSanitizer.NormalizeOptional(request.HumanContributionNotes, "Human contribution notes");
        }
        catch (Exception ex) when (ex is ArgumentException or ValidationException)
        {
            return BadRequest(V1ApiResponse<object>.Fail(ex.Message));
        }

        var youtubeUrl = request.YoutubeUrl?.Trim();
        if (!string.IsNullOrEmpty(youtubeUrl) && !IsLegacyYoutubeUrl(youtubeUrl))
            return BadRequest(V1ApiResponse<object>.Fail("Process video must be a YouTube URL (youtube.com or youtu.be)."));
        youtubeUrl = string.IsNullOrEmpty(youtubeUrl) ? null : youtubeUrl;

        var tools = (request.ToolsUsed ?? new List<string>())
            .Select(t => t?.Trim() ?? "")
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
        if (tools.Any(t => t.Length > 100))
            return BadRequest(V1ApiResponse<object>.Fail("Each tool name must be 100 characters or less."));
        if (tools.Count > 0 && System.Text.Json.JsonSerializer.Serialize(tools).Length > 2000)
            return BadRequest(V1ApiResponse<object>.Fail("Tools list is too long — remove some tools or shorten their names."));

        if (story is null && daw is null && vocalChain is null && promptNotes is null
            && productionNotes is null && humanContributionNotes is null
            && youtubeUrl is null && tools.Count == 0)
        {
            await _trackDetails.DeleteCreationProcessAsync(track.Id);
            return Ok(V1ApiResponse<object?>.Ok(null));
        }

        var saved = await _trackDetails.UpsertCreationProcessAsync(
            track.Id, story, daw, vocalChain, promptNotes, productionNotes, humanContributionNotes, youtubeUrl, tools);
        return Ok(V1ApiResponse<object>.Ok((object)saved));
    }

    // ───────────────────────── Proof videos (owner-only mutations) ─────────────────────────

    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpPost("proof-videos")]
    public async Task<IActionResult> AddProofVideo(string trackId, [FromBody] CreateProofVideoRequest request)
    {
        var userId = GetRequiredUserId();
        var track = await ResolveTrackAsync(trackId);
        if (track is null) return NotFound(V1ApiResponse<object>.Fail("Track not found."));
        if (!await IsOwnerAsync(track, userId))
            return StatusCode(403, V1ApiResponse<object>.Fail("You can only edit your own tracks."));

        if (!TryValidateVideo(request.VideoType, request.Url, out var normalizedUrl, out var error))
            return BadRequest(V1ApiResponse<object>.Fail(error!));

        string? title, description;
        try
        {
            title = MetadataSanitizer.NormalizeOptional(request.Title, "Title");
            description = MetadataSanitizer.NormalizeOptional(request.Description, "Description");
        }
        catch (Exception ex) when (ex is ArgumentException or ValidationException)
        {
            return BadRequest(V1ApiResponse<object>.Fail(ex.Message));
        }

        var visibility = NormalizeVisibility(request.Visibility) ?? "public";
        var sortOrder = request.SortOrder ?? await _trackDetails.GetNextProofVideoSortOrderAsync(track.Id);

        var saved = await _trackDetails.AddProofVideoAsync(track.Id, request.VideoType, normalizedUrl!, title, description, sortOrder, visibility);
        return StatusCode(201, V1ApiResponse<object>.Ok((object)saved));
    }

    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpPatch("proof-videos/{videoId:guid}")]
    public async Task<IActionResult> UpdateProofVideo(string trackId, Guid videoId, [FromBody] UpdateProofVideoRequest request)
    {
        var userId = GetRequiredUserId();
        var track = await ResolveTrackAsync(trackId);
        if (track is null) return NotFound(V1ApiResponse<object>.Fail("Track not found."));
        if (!await IsOwnerAsync(track, userId))
            return StatusCode(403, V1ApiResponse<object>.Fail("You can only edit your own tracks."));

        var existing = await _trackDetails.GetProofVideoAsync(track.Id, videoId);
        if (existing is null) return NotFound(V1ApiResponse<object>.Fail("Proof video not found."));

        // VideoType/Url change together so a switch from YouTube→External (or vice
        // versa) is always validated against the type it's actually becoming.
        var effectiveType = request.VideoType ?? existing.VideoType;
        string? normalizedUrl = null;
        if (request.VideoType is not null || request.Url is not null)
        {
            var urlToValidate = request.Url ?? existing.Url;
            if (!TryValidateVideo(effectiveType, urlToValidate, out normalizedUrl, out var error))
                return BadRequest(V1ApiResponse<object>.Fail(error!));
        }

        string? title, description;
        try
        {
            title = request.Title is null ? null : MetadataSanitizer.NormalizeAllowEmpty(request.Title, "Title");
            description = request.Description is null ? null : MetadataSanitizer.NormalizeAllowEmpty(request.Description, "Description");
        }
        catch (Exception ex) when (ex is ArgumentException or ValidationException)
        {
            return BadRequest(V1ApiResponse<object>.Fail(ex.Message));
        }

        string? visibility = null;
        if (request.Visibility is not null)
        {
            visibility = NormalizeVisibility(request.Visibility);
            if (visibility is null)
                return BadRequest(V1ApiResponse<object>.Fail("Visibility must be 'public' or 'hidden'."));
        }

        var saved = await _trackDetails.UpdateProofVideoAsync(
            track.Id, videoId,
            request.VideoType,
            normalizedUrl,
            title,
            description,
            request.SortOrder,
            visibility);

        if (saved is null) return NotFound(V1ApiResponse<object>.Fail("Proof video not found."));
        return Ok(V1ApiResponse<object>.Ok((object)saved));
    }

    [Authorize(Policy = "CanEditOwnTrack")]
    [HttpDelete("proof-videos/{videoId:guid}")]
    public async Task<IActionResult> DeleteProofVideo(string trackId, Guid videoId)
    {
        var userId = GetRequiredUserId();
        var track = await ResolveTrackAsync(trackId);
        if (track is null) return NotFound(V1ApiResponse<object>.Fail("Track not found."));
        if (!await IsOwnerAsync(track, userId))
            return StatusCode(403, V1ApiResponse<object>.Fail("You can only edit your own tracks."));

        var deleted = await _trackDetails.DeleteProofVideoAsync(track.Id, videoId);
        if (!deleted) return NotFound(V1ApiResponse<object>.Fail("Proof video not found."));

        return Ok(V1ApiResponse<object?>.Ok(null));
    }

    // ───────────────────────── Helpers ─────────────────────────

    private string? GetRequiredUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);

    private async Task<Track?> ResolveTrackAsync(string trackId)
    {
        if (Guid.TryParse(trackId, out var guid))
            return await _tracks.GetByIdAsync(guid);
        return await _tracks.GetByCambrianTrackIdAsync(trackId);
    }

    private async Task<bool> IsOwnerAsync(Track track, string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;
        if (track.CreatorId == userId) return true;
        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        return creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
    }

    private static string? NormalizeVisibility(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().ToLowerInvariant();
        return v is "public" or "hidden" ? v : null;
    }

    /// <summary>
    /// Validates a proof video's Url against its VideoType. YouTube uses strict
    /// video-id parsing (rejects channel/playlist/non-video links); External
    /// requires a well-formed absolute http(s) URL. Returns the normalized URL
    /// to persist (trimmed original — we don't rewrite creator-provided links).
    /// </summary>
    private static bool TryValidateVideo(string videoType, string? url, out string? normalizedUrl, out string? error)
    {
        normalizedUrl = null;
        error = null;
        var trimmed = url?.Trim();

        if (string.Equals(videoType, "YouTube", StringComparison.OrdinalIgnoreCase))
        {
            if (!YoutubeUrlValidator.TryExtractVideoId(trimmed, out _))
            {
                error = "Url must be a valid YouTube video link (e.g. https://www.youtube.com/watch?v=..., https://youtu.be/...).";
                return false;
            }
        }
        else if (string.Equals(videoType, "External", StringComparison.OrdinalIgnoreCase))
        {
            if (!ExternalVideoUrlValidator.IsValid(trimmed))
            {
                error = "Url must be a valid http(s) URL.";
                return false;
            }
        }
        else
        {
            error = "VideoType must be 'YouTube' or 'External'.";
            return false;
        }

        normalizedUrl = trimmed;
        return true;
    }

    private static readonly HashSet<string> LegacyYoutubeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be"
    };

    private static bool IsLegacyYoutubeUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == "https" || uri.Scheme == "http")
            && string.IsNullOrEmpty(uri.UserInfo)
            && LegacyYoutubeHosts.Contains(uri.Host);
    }
}
