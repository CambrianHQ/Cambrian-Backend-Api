using Cambrian.Api.Middleware;
using Cambrian.Api.Infrastructure;
using Cambrian.Api.Security;
using Cambrian.Application.DTOs.Creators;
using Cambrian.Application.Interfaces;
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
    private readonly ICreatorService _creatorService;
    private readonly IObjectStorage _storage;
    private readonly ITransactionManager _transactions;
    private readonly CreatorImageUploadGrantService _imageGrants;
    private readonly UserManager<Cambrian.Domain.Entities.ApplicationUser> _userManager;
    private readonly ILogger<CreatorsController> _logger;

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private const long MaxImageSize = 10 * 1024 * 1024; // 10 MB

    public CreatorsController(ICreatorIdentityRepository creators, ICreatorProfileRepository profiles, ICreatorService creatorService, IObjectStorage storage, ITransactionManager transactions, CreatorImageUploadGrantService imageGrants, UserManager<Cambrian.Domain.Entities.ApplicationUser> userManager, ILogger<CreatorsController> logger)
    {
        _creators = creators;
        _profiles = profiles;
        _creatorService = creatorService;
        _storage = storage;
        _transactions = transactions;
        _imageGrants = imageGrants;
        _userManager = userManager;
        _logger = logger;
    }

    // ───── GET /api/creators/dashboard ─────

    [HttpGet("dashboard")]
    [Authorize(Roles = "Creator,Admin")]
    public async Task<IActionResult> Dashboard()
    {
        var userId = GetRequiredUserId();
        if (userId is null) return Unauthorized();
        var dashboard = await _creatorService.GetDashboardAsync(userId);
        foreach (var t in dashboard.Tracks)
        {
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveImageUrl(t.CoverArtUrl);
        }
        return OkResponse(dashboard);
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
        ResolveCreatorImageUrls(creator);
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
        ResolveCreatorImageUrls(creator);
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
        ResolveCreatorImageUrls(creator);
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
        var paged = await _creators.GetTracksPagedByCreatorIdAsync(actualCreatorId, page, pageSize);
        ResolveTrackUrls(paged.Items);
        return Ok(ToPagedEnvelope(paged));
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

        // Use canonical resolver — handles UUID, ApplicationUser.Id, and username
        var creator = await _creators.ResolveByLegacyIdentifierAsync(slug);
        if (creator is null) return NotFoundResponse("Creator not found.");

        var creatorId = Guid.Parse(creator.Id);
        var paged = await _creators.GetTracksPagedByCreatorIdAsync(creatorId, page, pageSize);
        ResolveTrackUrls(paged.Items);
        return Ok(ToPagedEnvelope(paged));
    }

    private static Cambrian.Application.DTOs.Catalog.CatalogPageResponse ToPagedEnvelope(
        Cambrian.Application.DTOs.Catalog.PagedResult<Cambrian.Application.DTOs.Catalog.TrackResponse> paged) => new()
    {
        Data = paged.Items,
        Page = paged.Page,
        PageSize = paged.PageSize,
        TotalCount = paged.TotalCount,
        TotalPages = paged.TotalPages,
        HasNextPage = paged.HasNextPage,
        HasPreviousPage = paged.HasPreviousPage,
    };

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
        var userId = GetRequiredUserId()!;
        var creator = await _creators.GetByUserIdAsync(userId);
        if (creator is null)
        {
            // Creator row doesn't exist yet — user hasn't completed set-username.
            // Return a partial response from ApplicationUser so the frontend can
            // show profile data and prompt for username setup.
            var appUser = await _userManager.FindByIdAsync(userId);
            if (appUser is null) return NotFoundResponse("User not found.");
            return OkResponse(new
            {
                Id = (string?)null,
                Username = (string?)null,
                canChangeUsername = true,
                DisplayName = appUser.DisplayName ?? appUser.Email?.Split('@')[0] ?? "",
                Bio = appUser.Bio ?? "",
                ProfileImageUrl = ResolveImageUrl(appUser.ProfileImageUrl),
                CoverImageUrl = ResolveImageUrl(appUser.CoverImageUrl),
                SocialLinks = (object?)null,
                Stats = (object?)null,
                Tracks = Array.Empty<object>(),
                needsUsername = true
            });
        }
        // CreatorProfile is the source of truth for images — Creator table may be stale.
        var profile = await _profiles.GetByUserIdAsync(userId);
        return OkResponse(new
        {
            creator.Id,
            creator.Username,
            canChangeUsername = string.IsNullOrWhiteSpace(creator.Username),
            creator.DisplayName,
            creator.Bio,
            ProfileImageUrl = ResolveImageUrl(profile?.ProfileImageUrl ?? creator.ProfileImageUrl),
            CoverImageUrl = ResolveImageUrl(profile?.BannerImageUrl ?? creator.CoverImageUrl),
            creator.SocialLinks,
            creator.Stats,
            creator.Tracks
        });
    }

    // ───── PUT /api/creator/me ─────
    // Note: routed under /api/creator (singular) for "my profile" semantics

    [Authorize]
    [RequireCreatorTier]
    [HttpPut("/api/creator/me")]
    public async Task<IActionResult> UpdateMyProfile([FromBody] UpdateCreatorProfileRequest body)
    {
        var userId = GetRequiredUserId()!;

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

                // Check uniqueness in Creators table (excluding own record)
                var existingCreatorId = await _creators.GetCreatorIdForUserAsync(userId);
                var taken = await _creators.IsUsernameTakenAsync(normalized, existingCreatorId);
                // Also check Identity table — username must be globally unique
                var existingInIdentity = await _userManager.FindByNameAsync(normalized);
                if (taken || (existingInIdentity is not null && existingInIdentity.Id != userId))
                    return ConflictResponse("That username is already taken.");
            }
        }

        // Pass only identity fields to Creator table (username/displayName)
        var identityOnly = new UpdateCreatorProfileRequest
        {
            Username = body.Username,
            DisplayName = body.DisplayName,
            SocialLinks = body.SocialLinks,
        };

        await using var tx = await _transactions.BeginTransactionAsync();

        var saved = await _creators.UpsertAsync(userId, identityOnly);

        // Write canonical presentation fields to CreatorProfile (single source of truth).
        // No-op if the creator hasn't created a CreatorProfile yet; legacy Creator fields
        // serve as fallback at read time until a profile is created.
        var socialLinksJson = body.SocialLinks is not null
            ? System.Text.Json.JsonSerializer.Serialize(body.SocialLinks)
            : null;
        await _profiles.UpdatePresentationFieldsAsync(userId,
            body.Bio?.Trim(),
            socialLinksJson,
            body.CoverImageUrl?.Trim(),
            body.ProfileImageUrl?.Trim());

        // Mirror DisplayName, Bio, and images back to ApplicationUser for /auth/me consistency
        var appUser = await _userManager.FindByIdAsync(userId);
        if (appUser is not null)
        {
            var dirty = false;
            if (body.DisplayName is not null)
            {
                appUser.DisplayName = body.DisplayName.Trim();
                dirty = true;
            }
            if (body.Bio is not null)
            {
                // ApplicationUser.Bio is varchar(500) — store truncated mirror
                var trimmed = body.Bio.Trim();
                appUser.Bio = trimmed.Length > 500 ? trimmed[..497] + "..." : trimmed;
                dirty = true;
            }
            if (body.ProfileImageUrl is not null)
            {
                appUser.ProfileImageUrl = body.ProfileImageUrl.Trim();
                dirty = true;
            }
            if (body.CoverImageUrl is not null)
            {
                appUser.CoverImageUrl = body.CoverImageUrl.Trim();
                dirty = true;
            }
            if (dirty) await _userManager.UpdateAsync(appUser);
        }

        await _transactions.CommitAsync();

        // CreatorProfile is the source of truth for images — prefer it over Creator table
        var updatedProfile = await _profiles.GetByUserIdAsync(userId);
        saved.ProfileImageUrl = ResolveImageUrl(updatedProfile?.ProfileImageUrl ?? saved.ProfileImageUrl);
        saved.CoverImageUrl = ResolveImageUrl(updatedProfile?.BannerImageUrl ?? saved.CoverImageUrl);
        return OkResponse(saved);
    }

    // ───── POST /api/uploads/creator-image-url ─────

    // ───── POST /api/uploads/creator-image-url (JSON — presigned URL flow) ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("/api/uploads/creator-image-url")]
    public IActionResult CreateCreatorImageUploadUrl([FromBody] CreateImageUploadRequest body)
    {
        var purpose = (body.Type ?? "profile")
            .Replace("-image", "", StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();
        if (purpose is not ("profile" or "cover"))
            return ErrorResponse("Image purpose must be profile or cover.");

        var ext = Path.GetExtension(body.FileName ?? "").ToLowerInvariant();
        var contentType = body.ContentType?.Trim().ToLowerInvariant() ?? "";
        if (!AllowedImageExtensions.Contains(ext) || !ImageUploadValidator.IsSupported(ext, contentType))
        {
            return ErrorResponse("Only .jpg, .jpeg, .png, .webp images are allowed.");
        }

        var userId = GetRequiredUserId()!;
        var grant = _imageGrants.Issue(userId, purpose, ext, contentType);
        var publicUrl = _storage.GetPublicUrl(grant.Payload.Key);
        var uploadUrl =
            $"{Request.Scheme}://{Request.Host}/api/uploads/creator-image/{grant.Payload.Key}"
            + $"?grant={Uri.EscapeDataString(grant.Token)}";

        return OkResponse(new CreatorImageUploadResponse
        {
            UploadUrl = uploadUrl,
            PublicUrl = publicUrl,
            ExpiresAt = grant.Payload.ExpiresAt,
        });
    }

    // ───── PUT /api/uploads/creator-image/{**key} (local storage proxy) ─────

    [Authorize]
    [RequireCreatorTier]
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPut("/api/uploads/creator-image/{**key}")]
    [RequestSizeLimit(CreatorImageUploadGrantService.MaxImageBytes)]
    public async Task<IActionResult> ProxyCreatorImageUpload(string key, [FromQuery] string? grant)
    {
        if (string.IsNullOrWhiteSpace(key)
            || key.Contains("..", StringComparison.Ordinal)
            || (!key.StartsWith("creator-profiles/", StringComparison.Ordinal)
                && !key.StartsWith("creator-covers/", StringComparison.Ordinal)))
        {
            return ErrorResponse("Invalid upload key.");
        }

        if (Request.ContentLength is > CreatorImageUploadGrantService.MaxImageBytes)
            return ErrorResponse("Image must be under 10 MB.");

        var contentType = Request.ContentType?.Trim().ToLowerInvariant() ?? "";
        var extension = Path.GetExtension(key).ToLowerInvariant();
        byte[] imageBytes;
        try
        {
            imageBytes = await ImageUploadValidator.ReadAndValidateAsync(
                Request.Body,
                extension,
                contentType,
                CreatorImageUploadGrantService.MaxImageBytes,
                HttpContext.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            return ErrorResponse(ex.Message);
        }

        var userId = GetRequiredUserId()!;
        if (string.IsNullOrWhiteSpace(grant)
            || !_imageGrants.TryConsume(grant, userId, key, contentType, out var payload))
        {
            return ForbiddenResponse("Invalid, expired, or already-used upload grant.");
        }

        try
        {
            await using var stream = new MemoryStream(imageBytes, writable: false);
            await _storage.UploadAsync(stream, key, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Proxy image upload failed: key={Key}, storage={StorageType}",
                key, _storage.GetType().Name);
            return StatusCode(502, new { success = false, error = "Image upload failed. Storage may be misconfigured." });
        }

        var publicUrl = _storage.GetPublicUrl(key);
        if (payload.Purpose == "cover")
            await _profiles.UpdateImageAsync(userId, publicUrl, null);
        else
            await _profiles.UpdateImageAsync(userId, null, publicUrl);

        var appUser = await _userManager.FindByIdAsync(userId);
        if (appUser is not null)
        {
            if (payload.Purpose == "cover")
                appUser.CoverImageUrl = publicUrl;
            else
                appUser.ProfileImageUrl = publicUrl;
            await _userManager.UpdateAsync(appUser);
        }

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

        var purpose = type.Replace("-image", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (purpose is not ("profile" or "cover"))
            return ErrorResponse("Image purpose must be profile or cover.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var contentType = file.ContentType?.Trim().ToLowerInvariant() ?? "";
        if (!AllowedImageExtensions.Contains(ext) || !ImageUploadValidator.IsSupported(ext, contentType))
            return ErrorResponse("Only .jpg, .jpeg, .png, .webp images are allowed.");

        var userId = GetRequiredUserId()!;
        var folder = purpose == "cover" ? "creator-covers" : "creator-profiles";
        var key = $"{folder}/{userId}/{Guid.NewGuid():N}{ext}";

        await using var stream = file.OpenReadStream();
        byte[] imageBytes;
        try
        {
            imageBytes = await ImageUploadValidator.ReadAndValidateAsync(
                stream,
                ext,
                contentType,
                MaxImageSize,
                HttpContext.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            return ErrorResponse(ex.Message);
        }

        try
        {
            await using var validatedStream = new MemoryStream(imageBytes, writable: false);
            await _storage.UploadAsync(validatedStream, key, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creator image upload failed: key={Key}, storage={StorageType}",
                key, _storage.GetType().Name);
            return StatusCode(502, new { success = false, error = "Image upload failed. Storage may be misconfigured." });
        }
        var publicUrl = _storage.GetPublicUrl(key);

        // Canonical write: CreatorProfile is the source of truth for images
        if (purpose == "cover")
            await _profiles.UpdateImageAsync(userId, publicUrl, null);
        else
            await _profiles.UpdateImageAsync(userId, null, publicUrl);

        // Sync to ApplicationUser so /auth/me returns updated images
        var appUser = await _userManager.FindByIdAsync(userId);
        if (appUser is not null)
        {
            if (purpose == "cover")
                appUser.CoverImageUrl = publicUrl;
            else
                appUser.ProfileImageUrl = publicUrl;
            await _userManager.UpdateAsync(appUser);
        }

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

        var userId = GetRequiredUserId()!;
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

        var userId = GetRequiredUserId()!;
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
        var userId = GetRequiredUserId()!;
        var following = await _creators.IsFollowingAsync(userId, creatorId);
        var followerCount = await _creators.GetFollowerCountAsync(creatorId);
        return OkResponse(new { following, followerCount });
    }

    // ───── POST/GET /creators/search ─────

    /// <summary>
    /// Search creators by username or display name. Public; result set is capped server-side.
    /// Exposed at /creators/search (the path the frontend calls) for both POST (body) and GET (query).
    /// </summary>
    [HttpPost("/creators/search")]
    public Task<IActionResult> SearchCreatorsPost([FromBody] CreatorSearchRequest? body)
        => SearchCreatorsCore(body?.Query, body?.Limit);

    [HttpGet("/creators/search")]
    public Task<IActionResult> SearchCreatorsGet([FromQuery] string? query, [FromQuery] string? q, [FromQuery] int? limit)
        // Accept ?q= as an alias — an unbound param silently searches for
        // nothing and returns [], which reads as "search is broken".
        => SearchCreatorsCore(string.IsNullOrWhiteSpace(query) ? q : query, limit);

    private async Task<IActionResult> SearchCreatorsCore(string? query, int? limit)
    {
        if (string.IsNullOrWhiteSpace(query))
            return OkResponse(Array.Empty<CreatorSearchResultDto>());

        var results = await _creators.SearchAsync(query, limit ?? 20);
        foreach (var r in results)
            r.ProfileImageUrl = ResolveImageUrl(r.ProfileImageUrl);
        return OkResponse(results);
    }

    // ───── URL resolution helpers ─────

    private void ResolveTrackUrls(IEnumerable<Cambrian.Application.DTOs.Catalog.TrackResponse> tracks)
    {
        foreach (var t in tracks)
        {
            t.AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio");
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveImageUrl(t.CoverArtUrl);
            if (!string.IsNullOrEmpty(t.CreatorProfileImageUrl))
                t.CreatorProfileImageUrl = ResolveImageUrl(t.CreatorProfileImageUrl);
        }
    }

    private void ResolveCreatorImageUrls(Cambrian.Application.DTOs.Creators.PublicCreatorDto dto)
    {
        dto.ProfileImageUrl = ResolveImageUrl(dto.ProfileImageUrl);
        dto.CoverImageUrl = ResolveImageUrl(dto.CoverImageUrl);
        foreach (var t in dto.Tracks)
        {
            t.AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio");
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveImageUrl(t.CoverArtUrl);
        }
    }
}
