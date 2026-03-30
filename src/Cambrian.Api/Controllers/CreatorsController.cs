using System.Security.Claims;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Creators;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

/// <summary>
/// UUID-based creator identity endpoints.
/// Public responses never include email.
/// All track queries use creatorId (UUID FK) — never email or username.
/// </summary>
[Route("api/creators")]
public class CreatorsController : BaseController
{
    private readonly ICreatorIdentityRepository _creators;
    private readonly ICreatorProfileRepository _profiles;
    private readonly IObjectStorage _storage;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<CreatorsController> _logger;

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private const long MaxImageSize = 10 * 1024 * 1024; // 10 MB

    public CreatorsController(ICreatorIdentityRepository creators, ICreatorProfileRepository profiles, IObjectStorage storage, UserManager<ApplicationUser> userManager, ILogger<CreatorsController> logger)
    {
        _creators = creators;
        _profiles = profiles;
        _storage = storage;
        _userManager = userManager;
        _logger = logger;
    }

    // ───── GET /api/creators/{creatorId} ─────

    /// <summary>Get public creator profile by UUID. Falls back to ApplicationUser.Id lookup.</summary>
    [HttpGet("{creatorId:guid}")]
    public async Task<IActionResult> GetCreatorById(Guid creatorId)
    {
        var creator = await _creators.GetByIdAsync(creatorId);
        // Fallback: the caller may have passed an ApplicationUser.Id instead of creator UUID
        creator ??= await _creators.GetByUserIdAsync(creatorId.ToString());
        if (creator is null)
        {
            _logger.LogWarning("CreatorLookup: UUID={CreatorId} not found", creatorId);
            return NotFoundResponse("Creator not found.");
        }
        return OkResponse(creator);
    }

    // ───── GET /api/creators/by-username/{username} ─────

    /// <summary>
    /// Resolve username → creatorId, then return the same public DTO.
    /// Normalizes username before lookup.
    /// </summary>
    [HttpGet("by-username/{username}")]
    [HttpGet("/creator/username/{username}")]
    public async Task<IActionResult> GetCreatorByUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return ErrorResponse("Username is required.");

        var creator = await _creators.GetByUsernameAsync(username);
        if (creator is null)
        {
            _logger.LogWarning("CreatorLookup: username={Username} not found", username);
            return NotFoundResponse("Creator not found.");
        }
        return OkResponse(creator);
    }

    // ───── GET /api/creators/resolve/{identifier} ─────

    /// <summary>
    /// Compatibility resolver: accepts a legacy identifier (ApplicationUser.Id, UUID, or username)
    /// and returns the canonical creator profile. Use during migration to resolve old links.
    /// </summary>
    [HttpGet("resolve/{identifier}")]
    public async Task<IActionResult> ResolveCreator(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return ErrorResponse("Identifier is required.");

        var creator = await _creators.ResolveByLegacyIdentifierAsync(identifier);
        if (creator is null)
        {
            _logger.LogWarning("CreatorResolve: failed to resolve identifier={Identifier}", identifier);
            return NotFoundResponse("Creator not found.");
        }
        return OkResponse(creator);
    }

    // ───── GET /api/creators/{creatorId}/tracks ─────

    /// <summary>
    /// Get tracks by creatorId (UUID FK). Filters only by creatorId.
    /// Does NOT filter by email or username.
    /// </summary>
    [HttpGet("{creatorId:guid}/tracks")]
    public async Task<IActionResult> GetCreatorTracks(Guid creatorId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;

        var creator = await _creators.GetByIdAsync(creatorId);
        // Fallback: caller may have passed ApplicationUser.Id
        creator ??= await _creators.GetByUserIdAsync(creatorId.ToString());
        if (creator is null) return NotFoundResponse("Creator not found.");

        var actualCreatorId = Guid.Parse(creator.Id);
        var tracks = await _creators.GetTracksByCreatorIdAsync(actualCreatorId, page, pageSize);
        ResolveTrackUrls(tracks);
        return OkResponse(tracks);
    }

    // ───── GET /creator/tracks/{slug} ─────

    /// <summary>
    /// Get tracks by creator slug (username) or creator GUID.
    /// Accepts both a username string and a UUID; resolves to the canonical creator, then returns tracks.
    /// </summary>
    [HttpGet("/creator/tracks/{slug}")]
    public async Task<IActionResult> GetCreatorTracksBySlug(string slug,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return ErrorResponse("Creator slug is required.");

        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;

        // If the slug looks like a GUID, resolve by ID (or ApplicationUser.Id fallback)
        PublicCreatorDto? creator = null;
        if (Guid.TryParse(slug, out var parsedId))
        {
            creator = await _creators.GetByIdAsync(parsedId);
            creator ??= await _creators.GetByUserIdAsync(slug);
        }

        // Fallback: resolve by username
        creator ??= await _creators.GetByUsernameAsync(slug);
        if (creator is null) return NotFoundResponse("Creator not found.");

        var creatorId = Guid.Parse(creator.Id);
        var tracks = await _creators.GetTracksByCreatorIdAsync(creatorId, page, pageSize);
        ResolveTrackUrls(tracks);
        return OkResponse(tracks);
    }

    // ───── GET /api/creators/username-availability?username=... ─────

    /// <summary>Check if a username is available. Normalizes input.</summary>
    [EnableRateLimiting("auth")]
    [HttpGet("username-availability")]
    [HttpGet("/creator/username-availability")]
    public async Task<IActionResult> CheckUsernameAvailability([FromQuery] string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return ErrorResponse("Username is required.");

        var normalized = username.Trim().ToLowerInvariant();

        if (normalized.Length < 3 || normalized.Length > 40)
            return OkResponse(new UsernameAvailabilityResponse { Username = normalized, Available = false });

        var takenInCreators = await _creators.IsUsernameTakenAsync(normalized);
        // Also check Identity table — username must be globally unique
        var existingInIdentity = await _userManager.FindByNameAsync(normalized);
        var taken = takenInCreators || existingInIdentity is not null;
        return OkResponse(new UsernameAvailabilityResponse { Username = normalized, Available = !taken });
    }

    // ───── GET /api/creator/me ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpGet("/api/creator/me")]
    public async Task<IActionResult> GetMyCreatorProfile()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var creator = await _creators.GetByUserIdAsync(userId);
        if (creator is null) return NotFoundResponse("Creator profile not found.");
        return OkResponse(creator);
    }

    // ───── PUT /api/creator/me ─────
    // Note: routed under /api/creator (singular) for "my profile" semantics

    [Authorize]
    [RequireCreatorTier]
    [HttpPut("/api/creator/me")]
    public async Task<IActionResult> UpdateMyProfile([FromBody] UpdateCreatorProfileRequest body)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        // If no username provided, auto-populate from ApplicationUser.UserName
        // so users who already set it via POST /auth/set-username don't have to repeat it.
        if (string.IsNullOrWhiteSpace(body.Username))
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user?.UserName is not null
                && !user.UserName.Contains('@')          // skip if still set to email
                && user.UserName.Length >= 3)
            {
                body.Username = user.UserName;
            }
        }

        // Validate username if provided — but once set it cannot be changed.
        if (body.Username is not null)
        {
            var existingCreator = await _creators.GetByUserIdAsync(userId);
            if (existingCreator?.Username is not null)
            {
                // Username already set — silently keep the existing one (ignore the request field).
                body.Username = existingCreator.Username;
            }
            else
            {
                var normalized = body.Username.Trim().ToLowerInvariant();
                if (normalized.Length < 3 || normalized.Length > 40)
                    return ErrorResponse("Username must be between 3 and 40 characters.");

                // Check uniqueness (excluding own record)
                var existingCreatorId = await _creators.GetCreatorIdForUserAsync(userId);
                var taken = await _creators.IsUsernameTakenAsync(normalized, existingCreatorId);
                if (taken)
                    return ConflictResponse("That username is already taken.");
            }
        }

        var saved = await _creators.UpsertAsync(userId, body);

        // Sync image changes to CreatorProfile (marketplace reads from that table)
        if (!string.IsNullOrWhiteSpace(body.ProfileImageUrl))
            await _profiles.UpdateImageAsync(userId, null, body.ProfileImageUrl.Trim());
        if (!string.IsNullOrWhiteSpace(body.CoverImageUrl))
            await _profiles.UpdateImageAsync(userId, body.CoverImageUrl.Trim(), null);

        return OkResponse(saved);
    }

    // ───── POST /api/uploads/creator-image-url ─────

    // ───── POST /api/uploads/creator-image-url (JSON — presigned URL flow) ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("/api/uploads/creator-image-url")]
    public IActionResult CreateCreatorImageUploadUrl([FromBody] CreateImageUploadRequest body)
    {
        var type = (body.Type ?? "profile").Replace("-image", "");
        var ext = Path.GetExtension(body.FileName ?? ".jpg");
        if (!AllowedImageExtensions.Contains(ext.ToLowerInvariant()))
            return ErrorResponse("Only .jpg, .jpeg, .png, .webp images are allowed.");

        var folder = type == "cover" ? "creator-covers" : "creator-profiles";
        var key = $"{folder}/{Guid.NewGuid()}{ext}";
        var contentType = body.ContentType ?? "application/octet-stream";
        var publicUrl = _storage.GetPublicUrl(key);

        // Try presigned PUT URL (S3/R2); for local storage returns null
        var uploadUrl = _storage.GenerateUploadUrl(key, contentType);

        if (uploadUrl is null)
        {
            // Local storage: use proxy endpoint
            uploadUrl = $"{Request.Scheme}://{Request.Host}/api/uploads/creator-image/{key}";
        }

        return OkResponse(new CreatorImageUploadResponse
        {
            UploadUrl = uploadUrl,
            PublicUrl = publicUrl,
        });
    }

    // ───── PUT /api/uploads/creator-image/{**key} (local storage proxy) ─────

    [Authorize]
    [HttpPut("/api/uploads/creator-image/{**key}")]
    public async Task<IActionResult> ProxyCreatorImageUpload(string key)
    {
        if (Request.ContentLength is > MaxImageSize)
            return ErrorResponse("Image must be under 10 MB.");

        var contentType = Request.ContentType ?? "application/octet-stream";
        await _storage.UploadAsync(Request.Body, key, contentType);
        var publicUrl = _storage.GetPublicUrl(key);
        return OkResponse(new { publicUrl });
    }

    // ───── POST /api/uploads/creator-image (multipart fallback) ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("/api/uploads/creator-image")]
    public async Task<IActionResult> UploadCreatorImage(IFormFile file,
        [FromQuery] string type = "profile")
    {
        if (file is null || file.Length == 0)
            return ErrorResponse("File is required.");

        if (file.Length > MaxImageSize)
            return ErrorResponse("Image must be under 10 MB.");

        var ext = Path.GetExtension(file.FileName);
        if (!AllowedImageExtensions.Contains(ext))
            return ErrorResponse("Only .jpg, .jpeg, .png, .webp images are allowed.");

        var folder = type == "cover" ? "creator-covers" : "creator-profiles";
        var key = $"{folder}/{Guid.NewGuid()}{ext}";

        await using var stream = file.OpenReadStream();
        var contentType = file.ContentType ?? "application/octet-stream";
        await _storage.UploadAsync(stream, key, contentType);
        var publicUrl = _storage.GetPublicUrl(key);

        // Update Creator table
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var updated = await _creators.UpdateImageUrlAsync(userId, type, publicUrl);
        if (!updated) return NotFoundResponse("Create a profile first.");

        // Sync CreatorProfile table so marketplace displays the image
        if (type == "cover")
            await _profiles.UpdateImageAsync(userId, publicUrl, null);
        else
            await _profiles.UpdateImageAsync(userId, null, publicUrl);

        return OkResponse(new CreatorImageUploadResponse
        {
            UploadUrl = publicUrl,
            PublicUrl = publicUrl,
        });
    }

    // ───── POST /api/creators/{creatorId}/follow ─────

    /// <summary>Follow a creator. Idempotent.</summary>
    [Authorize]
    [HttpPost("{creatorId:guid}/follow")]
    public async Task<IActionResult> Follow(Guid creatorId)
    {
        var creator = await _creators.GetByIdAsync(creatorId);
        if (creator is null) return NotFoundResponse("Creator not found.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await _creators.FollowAsync(userId, creatorId);

        var followerCount = await _creators.GetFollowerCountAsync(creatorId);
        return OkResponse(new { following = true, followerCount });
    }

    // ───── DELETE /api/creators/{creatorId}/follow ─────

    /// <summary>Unfollow a creator. Idempotent.</summary>
    [Authorize]
    [HttpDelete("{creatorId:guid}/follow")]
    public async Task<IActionResult> Unfollow(Guid creatorId)
    {
        var creator = await _creators.GetByIdAsync(creatorId);
        if (creator is null) return NotFoundResponse("Creator not found.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await _creators.UnfollowAsync(userId, creatorId);

        var followerCount = await _creators.GetFollowerCountAsync(creatorId);
        return OkResponse(new { following = false, followerCount });
    }

    // ───── GET /api/creators/{creatorId}/follow ─────

    /// <summary>Check if the authenticated user is following a creator.</summary>
    [Authorize]
    [HttpGet("{creatorId:guid}/follow")]
    public async Task<IActionResult> GetFollowStatus(Guid creatorId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var following = await _creators.IsFollowingAsync(userId, creatorId);
        var followerCount = await _creators.GetFollowerCountAsync(creatorId);
        return OkResponse(new { following, followerCount });
    }

    // ───── URL resolution helpers ─────

    private void ResolveTrackUrls(IEnumerable<Cambrian.Application.DTOs.Catalog.TrackResponse> tracks)
    {
        foreach (var t in tracks)
        {
            t.AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio");
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveCoverArtUrl(t.CoverArtUrl);
        }
    }

    private string ResolveCoverArtUrl(string rawUrl)
    {
        if (rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return rawUrl;
        return ResolveAbsoluteUrl(rawUrl);
    }
}
