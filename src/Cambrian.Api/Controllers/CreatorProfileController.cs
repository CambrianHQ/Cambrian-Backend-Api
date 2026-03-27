using System.Text.Json;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Creator profile management. Gate UI behind feature flag "creator_profiles".
/// </summary>
[Route("creator-profile")]
public class CreatorProfileController : BaseController
{
    private readonly ICreatorProfileRepository _profiles;
    private readonly IObjectStorage _storage;
    private readonly IStorefrontService _storefront;
    private readonly IFeatureFlagRepository _flags;

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private const long MaxImageSize = 10 * 1024 * 1024; // 10 MB

    public CreatorProfileController(ICreatorProfileRepository profiles, IObjectStorage storage, IStorefrontService storefront, IFeatureFlagRepository flags)
    {
        _profiles = profiles;
        _storage = storage;
        _storefront = storefront;
        _flags = flags;
    }

    // ───── Public: view a creator profile by slug ─────

    [HttpGet("{slug}")]
    public async Task<IActionResult> GetBySlug(string slug)
    {
        var profile = await _profiles.GetBySlugAsync(slug);
        if (profile is null) return NotFoundResponse("Creator not found.");
        return OkResponse(profile);
    }

    // ───── Public: full storefront (profile + stats + pinned + collections + tracks) ─────

    [HttpGet("{slug}/storefront")]
    public async Task<IActionResult> GetStorefront(string slug)
    {
        if (!await _flags.IsEnabledAsync("creator_storefront"))
            return NotFoundResponse("Storefront is not available.");

        var storefront = await _storefront.GetStorefrontAsync(slug);
        if (storefront is null) return NotFoundResponse("Creator not found.");
        return OkResponse(storefront);
    }

    // ───── Authenticated creator: get own profile ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpGet("me")]
    public async Task<IActionResult> GetMyProfile()
    {
        var userId = GetRequiredUserId()!;
        var profile = await _profiles.GetByUserIdAsync(userId);
        if (profile is null)
            return OkResponse(new { exists = false });
        return OkResponse(profile);
    }

    // ───── Upsert profile (bio, niche, social links, stats toggles) ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPut("me")]
    public async Task<IActionResult> UpsertProfile([FromBody] UpsertCreatorProfileRequest body)
    {
        var userId = GetRequiredUserId()!;

        var slug = body.Slug?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(slug))
            return ErrorResponse("Slug is required.");

        if (slug.Length < 3 || slug.Length > 100)
            return ErrorResponse("Slug must be between 3 and 100 characters.");

        // Check slug uniqueness
        var slugOwner = await _profiles.GetBySlugAsync(slug);
        if (slugOwner is not null && slugOwner.UserId != userId)
            return ConflictResponse("That slug is already taken.");

        var socialLinksJson = body.SocialLinks is not null
            ? JsonSerializer.Serialize(body.SocialLinks)
            : null;

        var saved = await _profiles.UpsertAsync(userId, slug, body.Bio?.Trim() ?? "",
            body.Niche?.Trim(), socialLinksJson, body.ShowEarnings, body.ShowDownloadStats);

        return OkResponse(saved);
    }

    // ───── Upload banner image ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("me/cover-image-upload")]
    public async Task<IActionResult> UploadBanner(IFormFile file)
    {
        var url = await UploadImage(file, "banners");
        if (url is null) return ErrorResponse("Invalid image file.");

        var userId = GetRequiredUserId()!;
        var existing = await _profiles.GetByUserIdAsync(userId);
        if (existing is null) return NotFoundResponse("Create a profile first.");

        var updated = await _profiles.UpdateImageAsync(userId, url, null);
        return OkResponse(new { bannerImageUrl = updated.BannerImageUrl });
    }

    // ───── Upload profile image ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("me/profile-image-upload")]
    [HttpPost("/settings/profile/avatar")]
    public async Task<IActionResult> UploadAvatar(IFormFile file)
    {
        var url = await UploadImage(file, "avatars");
        if (url is null) return ErrorResponse("Invalid image file. Accepted: jpg, jpeg, png, webp (max 10 MB).");

        var userId = GetRequiredUserId()!;
        // UpdateImageAsync auto-creates a minimal profile if one does not yet exist
        var updated = await _profiles.UpdateImageAsync(userId, null, url);
        return OkResponse(new { profileImageUrl = updated.ProfileImageUrl });
    }

    // ───── Collections: list ─────

    [HttpGet("{slug}/collections")]
    public async Task<IActionResult> GetCollections(string slug)
    {
        var profile = await _profiles.GetBySlugAsync(slug);
        if (profile is null) return NotFoundResponse("Creator not found.");

        var collections = await _profiles.GetCollectionsAsync(profile.UserId);
        return OkResponse(collections);
    }

    // ───── Collections: create ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("me/collections")]
    public async Task<IActionResult> CreateCollection([FromBody] UpsertCollectionRequest body)
    {
        var userId = GetRequiredUserId()!;
        if (string.IsNullOrWhiteSpace(body.Title))
            return ErrorResponse("Title is required.");

        var saved = await _profiles.AddCollectionAsync(userId, body.Title.Trim(),
            body.Description?.Trim(), body.TrackIds ?? "");
        return CreatedResponse(saved);
    }

    // ───── Collections: update ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPut("me/collections/{collectionId}")]
    public async Task<IActionResult> UpdateCollection(Guid collectionId, [FromBody] UpsertCollectionRequest body)
    {
        var userId = GetRequiredUserId()!;
        var owner = await _profiles.GetCollectionOwnerAsync(collectionId);
        if (owner is null) return NotFoundResponse("Collection not found.");
        if (owner != userId) return ForbiddenResponse();

        var saved = await _profiles.UpdateCollectionAsync(collectionId, userId,
            body.Title?.Trim(), body.Description?.Trim(), body.TrackIds);
        return OkResponse(saved);
    }

    // ───── Collections: delete ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpDelete("me/collections/{collectionId}")]
    public async Task<IActionResult> DeleteCollection(Guid collectionId)
    {
        var userId = GetRequiredUserId()!;
        var owner = await _profiles.GetCollectionOwnerAsync(collectionId);
        if (owner is null) return NotFoundResponse("Collection not found.");
        if (owner != userId) return ForbiddenResponse();

        await _profiles.DeleteCollectionAsync(collectionId);
        return MessageResponse("Collection deleted.");
    }

    // ───── Pinned tracks: update pinned track order ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPut("me/pinned-tracks")]
    public async Task<IActionResult> UpdatePinnedTracks([FromBody] UpdatePinnedTracksRequest body)
    {
        var userId = GetRequiredUserId()!;
        var existing = await _profiles.GetByUserIdAsync(userId);
        if (existing is null) return NotFoundResponse("Create a profile first.");

        var updated = await _profiles.UpdatePinnedTracksAsync(userId, body.TrackIds ?? "");
        return OkResponse(new { pinnedTrackIds = updated.PinnedTrackIds });
    }

    // ───── Helpers ─────

    private async Task<string?> UploadImage(IFormFile? file, string folder)
    {
        if (file is null || file.Length == 0) return null;
        if (file.Length > MaxImageSize) return null;

        var ext = Path.GetExtension(file.FileName)?.ToLowerInvariant() ?? "";
        if (!AllowedImageExtensions.Contains(ext)) return null;

        var key = $"{folder}/{Guid.NewGuid()}{ext}";
        await using var stream = file.OpenReadStream();
        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
        await _storage.UploadAsync(stream, key, contentType);
        return _storage.GenerateSignedUrl(key);
    }
}
