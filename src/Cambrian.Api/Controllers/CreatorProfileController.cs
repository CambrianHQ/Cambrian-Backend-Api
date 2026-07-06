using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.DTOs.Creators;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Creator profile management. Gate UI behind feature flag "creator_profiles".
/// </summary>
[Route("creator-profile")]
[EnableRateLimiting("auth")]
public class CreatorProfileController : BaseController
{
    private readonly ICreatorProfileRepository _profiles;
    private readonly IObjectStorage _storage;
    private readonly IStorefrontService _storefront;
    private readonly IFeatureFlagRepository _flags;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ITrackRepository _tracks;
    private readonly ITransactionManager _transactions;
    private readonly ICatalogService _catalog;
    private readonly ITrackVisibilityPolicy _trackVisibility;
    private readonly UserManager<Cambrian.Domain.Entities.ApplicationUser> _userManager;
    private readonly ILogger<CreatorProfileController> _logger;

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private const long MaxImageSize = 10 * 1024 * 1024; // 10 MB

    public CreatorProfileController(ICreatorProfileRepository profiles, IObjectStorage storage, IStorefrontService storefront, IFeatureFlagRepository flags, ICreatorIdentityRepository creators, ITrackRepository tracks, ITransactionManager transactions, ICatalogService catalog, ITrackVisibilityPolicy trackVisibility, UserManager<Cambrian.Domain.Entities.ApplicationUser> userManager, ILogger<CreatorProfileController> logger)
    {
        _profiles = profiles;
        _storage = storage;
        _storefront = storefront;
        _flags = flags;
        _creators = creators;
        _tracks = tracks;
        _transactions = transactions;
        _catalog = catalog;
        _trackVisibility = trackVisibility;
        _userManager = userManager;
        _logger = logger;
    }

    // Returns false if any track ID in the comma-separated list does not belong to userId.
    private async Task<bool> AllTracksOwnedByCreatorAsync(string? trackIds, string userId)
    {
        if (string.IsNullOrWhiteSpace(trackIds)) return true;
        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ids = trackIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in ids)
        {
            if (!Guid.TryParse(segment.Trim(), out var trackId))
                return false; // malformed ID
            var track = await _tracks.GetByIdAsync(trackId);
            if (track is null) return false;
            var ownsLegacy = string.Equals(track.CreatorId, userId, StringComparison.Ordinal);
            var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
            if (!ownsLegacy && !ownsUuid) return false;
        }
        return true;
    }

    // ───── Public: view a creator profile by slug ─────

    [HttpGet("{slug}")]
    public async Task<IActionResult> GetBySlug(string slug)
    {
        var profile = await _profiles.GetBySlugAsync(slug);

        // Fall back: resolve by UUID, ApplicationUser.Id, or username
        if (profile is null)
        {
            var creator = await _creators.ResolveByLegacyIdentifierAsync(slug);
            if (creator is not null)
                profile = await _profiles.GetByUserIdAsync(creator.UserId);
        }

        if (profile is null) return NotFoundResponse("Creator not found.");
        profile.ProfileImageUrl = ResolveImageUrl(profile.ProfileImageUrl);
        profile.BannerImageUrl = ResolveImageUrl(profile.BannerImageUrl);
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
        storefront.Profile.ProfileImageUrl = ResolveImageUrl(storefront.Profile.ProfileImageUrl);
        storefront.Profile.BannerImageUrl = ResolveImageUrl(storefront.Profile.BannerImageUrl);
        foreach (var t in storefront.Tracks)
        {
            t.AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio");
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveImageUrl(t.CoverArtUrl);
        }
        foreach (var t in storefront.PinnedTracks)
        {
            t.AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio");
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveImageUrl(t.CoverArtUrl);
        }
        ResolveCollectionImageUrls(storefront.Collections);
        return OkResponse(storefront);
    }

    // ───── Authenticated creator: get own profile ─────

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetMyProfile()
    {
        var userId = GetRequiredUserId()!;
        var profile = await _profiles.GetByUserIdAsync(userId);
        if (profile is null)
            return OkResponse(new { exists = false });

        // Look up creator identity to determine username immutability.
        var creator = await _creators.GetByUserIdAsync(userId);
        var hasUsername = !string.IsNullOrWhiteSpace(creator?.Username);

        return OkResponse(new
        {
            profile.Id,
            profile.UserId,
            profile.Slug,
            username = creator?.Username ?? profile.Username,
            displayName = creator?.DisplayName ?? profile.DisplayName,
            canChangeUsername = !hasUsername,
            profile.Bio,
            profile.Niche,
            ProfileImageUrl = ResolveImageUrl(profile.ProfileImageUrl),
            BannerImageUrl = ResolveImageUrl(profile.BannerImageUrl),
            profile.SocialLinks,
            profile.StudioSetup,
            profile.JourneyEntries,
            profile.ShowEarnings,
            profile.ShowDownloadStats,
            profile.PinnedTrackIds,
            profile.Stats,
            profile.CreatedAt,
            profile.UpdatedAt
        });
    }

    // ───── Upsert profile (bio, niche, social links, stats toggles) ─────

    [Authorize]
    [RequireCreatorTier]
    [RequireUsername]
    [HttpPut("me")]
    public async Task<IActionResult> UpsertProfile([FromBody] UpsertCreatorProfileRequest body)
    {
        var userId = GetRequiredUserId()!;

        // Load the canonical creator identity once — used for slug derivation and DisplayName writes.
        var creator = await _creators.GetByUserIdAsync(userId);

        // Auto-derive slug from the creator's username when not explicitly provided.
        var slug = body.Slug?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(slug))
            slug = creator?.Username?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(slug))
            return ErrorResponse("Slug is required. Set a username first via POST /auth/set-username.");

        if (slug.Length < 3 || slug.Length > 100)
            return ErrorResponse("Slug must be between 3 and 100 characters.");

        // Check slug uniqueness
        var slugOwner = await _profiles.GetBySlugAsync(slug);
        if (slugOwner is not null && slugOwner.UserId != userId)
            return ConflictResponse("That slug is already taken.");

        // Validate social link URLs
        if (body.SocialLinks is not null)
        {
            foreach (var link in body.SocialLinks)
            {
                if (string.IsNullOrWhiteSpace(link.Url)) continue;
                if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != "https" && uri.Scheme != "http"))
                    return ErrorResponse($"Invalid URL for {link.Platform}: must be a valid http(s) URL.");
                // Reject javascript:, data:, or URLs with userinfo (e.g. http://evil@host)
                if (!string.IsNullOrEmpty(uri.UserInfo))
                    return ErrorResponse($"Invalid URL for {link.Platform}: user credentials in URLs are not allowed.");
            }
        }

        // H3: Enforce bio length limit (matches DB varchar(2000))
        if (body.Bio is not null && body.Bio.Length > 2000)
            return ErrorResponse("Bio must be 2000 characters or less.");

        if (body.DisplayName is not null && body.DisplayName.Trim().Length > 100)
            return ErrorResponse("Display name must be 100 characters or less.");

        var socialLinksJson = body.SocialLinks is not null
            ? JsonSerializer.Serialize(body.SocialLinks)
            : null;

        // "What's in my studio" — free-text/tag fields by design (niche gear must
        // never be blocked by a dropdown taxonomy). Normalize tags, cap sizes.
        string? studioSetupJson = null;
        if (body.StudioSetup is not null)
        {
            var studioError = CreatorProfileSectionValidator.NormalizeStudioSetup(body.StudioSetup);
            if (studioError is not null) return ErrorResponse(studioError);
            studioSetupJson = JsonSerializer.Serialize(body.StudioSetup);
            if (studioSetupJson.Length > 8000)
                return ErrorResponse("Studio setup is too large. Trim some entries.");
        }

        // Artist journey timeline entries.
        string? journeyEntriesJson = null;
        if (body.JourneyEntries is not null)
        {
            var journeyError = CreatorProfileSectionValidator.NormalizeJourneyEntries(body.JourneyEntries);
            if (journeyError is not null) return ErrorResponse(journeyError);
            journeyEntriesJson = JsonSerializer.Serialize(body.JourneyEntries);
            if (journeyEntriesJson.Length > 16000)
                return ErrorResponse("Journey timeline is too large. Remove some entries.");
        }

        await using var tx = await _transactions.BeginTransactionAsync();

        var saved = await _profiles.UpsertAsync(userId, slug, body.Bio?.Trim() ?? "",
            body.Niche?.Trim(), socialLinksJson, body.ShowEarnings, body.ShowDownloadStats,
            studioSetupJson: studioSetupJson, journeyEntriesJson: journeyEntriesJson);

        var displayName = body.DisplayName?.Trim();

        // Persist DisplayName to the canonical Creator identity row (read first by GetMyProfile and
        // /auth/me) and mirror DisplayName + Bio to ApplicationUser so every read surface is consistent.
        if (body.Bio is not null || !string.IsNullOrEmpty(displayName))
        {
            var appUser = await _userManager.FindByIdAsync(userId);

            if (!string.IsNullOrEmpty(displayName))
            {
                await _creators.UpsertAsync(userId, new UpdateCreatorProfileRequest
                {
                    DisplayName = displayName,
                    // Username is immutable once set; this also supplies the value if the Creator
                    // row must be created (username set without a Creator row yet).
                    Username = creator?.Username ?? appUser?.UserName,
                });
                // Echo the new name in the PUT response (CreatorProfileDto is the returned shape).
                saved.DisplayName = displayName;
            }

            if (appUser is not null)
            {
                if (body.Bio is not null)
                {
                    // ApplicationUser.Bio is varchar(500) — store truncated mirror; CreatorProfile is canonical
                    appUser.Bio = body.Bio.Trim().Length > 500
                        ? body.Bio.Trim()[..497] + "..."
                        : body.Bio.Trim();
                }
                if (!string.IsNullOrEmpty(displayName))
                    appUser.DisplayName = displayName;
                await _userManager.UpdateAsync(appUser);
            }
        }

        await _transactions.CommitAsync();

        saved.ProfileImageUrl = ResolveImageUrl(saved.ProfileImageUrl);
        saved.BannerImageUrl = ResolveImageUrl(saved.BannerImageUrl);
        return OkResponse(saved);
    }

    // ───── Partial update: toggle showEarnings / showDownloadStats ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPatch("me/settings")]
    public async Task<IActionResult> PatchSettings([FromBody] PatchProfileSettingsRequest body)
    {
        var userId = GetRequiredUserId()!;
        var updated = await _profiles.UpdateSettingsAsync(userId, body.ShowEarnings, body.ShowDownloadStats);
        if (updated is null) return NotFoundResponse("Create a profile first.");
        return OkResponse(updated);
    }

    // ───── Upload banner image ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("me/cover-image-upload")]
    [HttpPost("me/banner")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadBanner(IFormFile file)
    {
        string? url;
        try
        {
            url = await UploadImage(file, "banners");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Banner upload failed: storage={StorageType}", _storage.GetType().Name);
            return StatusCode(502, new { success = false, error = "Image upload failed. Storage may be misconfigured." });
        }
        if (url is null) return ErrorResponse("Invalid image file. Accepted: jpg, jpeg, png, webp (max 10 MB).");

        var userId = GetRequiredUserId()!;
        try
        {
            // UpdateImageAsync auto-creates the CreatorProfile if one doesn't exist yet
            var updated = await _profiles.UpdateImageAsync(userId, url, null);

            // Sync back to ApplicationUser so /auth/me and settings read the updated image
            var appUser = await _userManager.FindByIdAsync(userId);
            if (appUser is not null)
            {
                appUser.CoverImageUrl = url;
                await _userManager.UpdateAsync(appUser);
            }

            return OkResponse(new
            {
                bannerImageUrl = ResolveImageUrl(updated.BannerImageUrl),
                coverImageUrl = ResolveImageUrl(updated.BannerImageUrl),   // alias — frontend may use either name
                profileImageUrl = ResolveImageUrl(updated.ProfileImageUrl),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Banner image save failed after upload: userId={UserId}", userId);
            return StatusCode(500, new { success = false, error = "Image was uploaded but profile update failed. Please try again." });
        }
    }

    // ───── Upload profile image ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("me/profile-image-upload")]
    [HttpPost("me/avatar")]
    [HttpPost("/settings/profile/avatar")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadAvatar(IFormFile file)
    {
        string? url;
        try
        {
            url = await UploadImage(file, "avatars");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Avatar upload failed: storage={StorageType}", _storage.GetType().Name);
            return StatusCode(502, new { success = false, error = "Image upload failed. Storage may be misconfigured." });
        }
        if (url is null) return ErrorResponse("Invalid image file. Accepted: jpg, jpeg, png, webp (max 10 MB).");

        var userId = GetRequiredUserId()!;
        try
        {
            // UpdateImageAsync auto-creates the CreatorProfile if one doesn't exist yet
            var updated = await _profiles.UpdateImageAsync(userId, null, url);

            // Sync back to ApplicationUser so /auth/me and settings read the updated image
            var appUser = await _userManager.FindByIdAsync(userId);
            if (appUser is not null)
            {
                appUser.ProfileImageUrl = url;
                await _userManager.UpdateAsync(appUser);
            }

            return OkResponse(new
            {
                profileImageUrl = ResolveImageUrl(updated.ProfileImageUrl),
                coverImageUrl = ResolveImageUrl(updated.BannerImageUrl),   // include both fields for consistency
                bannerImageUrl = ResolveImageUrl(updated.BannerImageUrl),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Avatar image save failed after upload: userId={UserId}", userId);
            return StatusCode(500, new { success = false, error = "Image was uploaded but profile update failed. Please try again." });
        }
    }

    // ───── Collections: list ─────

    [HttpGet("{slug}/collections")]
    [HttpGet("/creator/username/{slug}/collections")]
    public async Task<IActionResult> GetCollections(string slug)
    {
        // Resolve the creator the same way GetBySlug does: profile slug first, then fall back to
        // the canonical identity resolver (handles username, UUID, ApplicationUser.Id). The
        // /creator/username/{slug} route passes a username, which often differs from the profile
        // slug (e.g. "e2e_keep_run" vs "e2e-keep-run"), which previously 404'd a real creator.
        var profile = await _profiles.GetBySlugAsync(slug);
        var creatorUserId = profile?.UserId;
        if (creatorUserId is null)
        {
            var creator = await _creators.ResolveByLegacyIdentifierAsync(slug);
            creatorUserId = creator?.UserId;
        }
        if (creatorUserId is null) return NotFoundResponse("Creator not found.");

        var collections = await _profiles.GetCollectionsAsync(creatorUserId);
        // Hidden albums are owner-only on the public listing.
        var requesterId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.Equals(requesterId, creatorUserId, StringComparison.Ordinal))
            collections = collections.Where(c => c.Visibility != "hidden").ToList();
        ResolveCollectionImageUrls(collections);
        return OkResponse(collections);
    }

    // ───── Collections: public album detail ─────

    /// <summary>
    /// Public album page payload: album metadata + hydrated public track
    /// projections + creator summary. Anyone can view a public album; hidden
    /// albums 404 for everyone but their owner. Tracks the viewer can't see
    /// (e.g. drafts) are filtered out of the hydrated list.
    /// </summary>
    [HttpGet("/collections/{collectionId:guid}")]
    public async Task<IActionResult> GetCollectionDetail(Guid collectionId)
    {
        var collection = await _profiles.GetCollectionByIdAsync(collectionId);
        if (collection is null) return NotFoundResponse("Album not found.");

        var owner = await _profiles.GetCollectionOwnerAsync(collectionId);
        var requesterId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var isOwner = owner is not null && string.Equals(requesterId, owner, StringComparison.Ordinal);
        if (collection.Visibility == "hidden" && !isOwner)
            return NotFoundResponse("Album not found.");

        var isAdmin = User?.IsInRole("Admin") == true;
        var tracks = new List<PublicCatalogTrackDto>();
        // Sequential on purpose — one scoped DbContext, no concurrent queries.
        foreach (var trackId in collection.TrackIds)
        {
            var track = await _catalog.GetTrackAsync(trackId);
            if (track is null) continue;
            if (!_trackVisibility.CanAccess(track.Visibility ?? "public", track.CreatorId, requesterId, isAdmin))
                continue;
            track.AudioUrl = ResolveAbsoluteUrl($"/stream/{track.Id}/audio");
            if (!string.IsNullOrEmpty(track.CoverArtUrl))
                track.CoverArtUrl = ResolveImageUrl(track.CoverArtUrl);
            tracks.Add(PublicCatalogTrackDto.From(track));
        }

        var creatorSummary = new CollectionCreatorSummary { UserId = owner ?? "" };
        if (owner is not null)
        {
            var profile = await _profiles.GetByUserIdAsync(owner);
            var identity = await _creators.ResolveByLegacyIdentifierAsync(owner);
            creatorSummary.CreatorId = identity?.Id;
            creatorSummary.Username = identity?.Username;
            creatorSummary.Slug = profile?.Slug ?? identity?.Username;
            creatorSummary.DisplayName = profile?.DisplayName ?? identity?.DisplayName ?? identity?.Username;
            var profileImage = profile?.ProfileImageUrl ?? identity?.ProfileImageUrl;
            creatorSummary.ProfileImageUrl = string.IsNullOrWhiteSpace(profileImage) ? null : ResolveImageUrl(profileImage);
        }

        ResolveCollectionImageUrl(collection);
        return OkResponse(new TrackCollectionDetailResponse
        {
            Id = collection.Id,
            Title = collection.Title,
            Slug = collection.Slug,
            Description = collection.Description,
            CoverImageUrl = collection.CoverImageUrl,
            Visibility = collection.Visibility,
            ReleaseDate = collection.ReleaseDate,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt,
            Creator = creatorSummary,
            Tracks = tracks,
            TrackIds = collection.TrackIds,
        });
    }

    // ───── Collections: list own (authenticated) ─────

    [Authorize]
    [HttpGet("me/collections")]
    public async Task<IActionResult> GetMyCollections()
    {
        var userId = GetRequiredUserId()!;
        var collections = await _profiles.GetCollectionsAsync(userId);
        ResolveCollectionImageUrls(collections);
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

        // H3: Only the creator's own tracks may be added to their collection.
        if (!await AllTracksOwnedByCreatorAsync(body.TrackIds, userId))
            return ErrorResponse("One or more tracks do not belong to you.");

        if (!TryParseCollectionVisibility(body.Visibility, out var visibility))
            return ErrorResponse("Visibility must be 'public' or 'hidden'.");
        if (!TryParseReleaseDate(body.ReleaseDate, out var releaseDate, out _))
            return ErrorResponse("ReleaseDate must be a valid ISO-8601 date.");

        var saved = await _profiles.AddCollectionAsync(userId, body.Title.Trim(),
            body.Description?.Trim(), body.CoverImageUrl?.Trim(), body.TrackIds ?? "",
            visibility, releaseDate);
        ResolveCollectionImageUrl(saved);
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

        // H3: Only the creator's own tracks may be added to their collection.
        if (!await AllTracksOwnedByCreatorAsync(body.TrackIds, userId))
            return ErrorResponse("One or more tracks do not belong to you.");

        if (!TryParseCollectionVisibility(body.Visibility, out var visibility))
            return ErrorResponse("Visibility must be 'public' or 'hidden'.");
        if (!TryParseReleaseDate(body.ReleaseDate, out var releaseDate, out var clearReleaseDate))
            return ErrorResponse("ReleaseDate must be a valid ISO-8601 date.");

        var saved = await _profiles.UpdateCollectionAsync(collectionId, userId,
            body.Title?.Trim(), body.Description?.Trim(), body.CoverImageUrl?.Trim(), body.TrackIds,
            visibility, releaseDate, clearReleaseDate);
        ResolveCollectionImageUrl(saved);
        return OkResponse(saved);
    }

    // ───── Collections: upload album artwork ─────

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("me/collections/{collectionId}/cover")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadCollectionCover(Guid collectionId, IFormFile file)
    {
        var userId = GetRequiredUserId()!;
        var owner = await _profiles.GetCollectionOwnerAsync(collectionId);
        if (owner is null) return NotFoundResponse("Collection not found.");
        if (owner != userId) return ForbiddenResponse();

        string? uploadedUrl;
        try
        {
            uploadedUrl = await UploadImage(file, "covers");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Album cover upload failed for collection {CollectionId}", collectionId);
            return StatusCode(502, new { success = false, error = "Image upload failed. Storage may be misconfigured." });
        }

        if (uploadedUrl is null)
            return ErrorResponse("Upload a JPG, PNG, or WebP image up to 10 MB.");

        var saved = await _profiles.UpdateCollectionAsync(collectionId, userId, null, null, uploadedUrl, null);
        ResolveCollectionImageUrl(saved);
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
        return NoContent();
    }

    // ───── Pinned tracks: update pinned track order ─────

    [Authorize]
    [RequireCreatorTier]
    [RequireUsername]
    [HttpPut("me/pinned-tracks")]
    public async Task<IActionResult> UpdatePinnedTracks([FromBody] UpdatePinnedTracksRequest body)
    {
        var userId = GetRequiredUserId()!;
        var existing = await _profiles.GetByUserIdAsync(userId);
        if (existing is null) return NotFoundResponse("Create a profile first.");

        var updated = await _profiles.UpdatePinnedTracksAsync(userId, body.TrackIds ?? "");
        return OkResponse(new { pinnedTrackIds = updated.PinnedTrackIds });
    }

    // ───── Follow a creator by slug ─────

    [Authorize]
    [HttpPost("{slug}/follow")]
    public async Task<IActionResult> Follow(string slug)
    {
        var creator = await _creators.GetByUsernameAsync(slug);
        if (creator is null) return NotFoundResponse("Creator not found.");
        if (!Guid.TryParse(creator.Id, out var creatorGuid)) return ErrorResponse("Invalid creator ID.");

        var userId = GetRequiredUserId()!;
        await _creators.FollowAsync(userId, creatorGuid);

        var followerCount = await _creators.GetFollowerCountAsync(creatorGuid);
        return OkResponse(new { following = true, followerCount });
    }

    [Authorize]
    [HttpDelete("{slug}/follow")]
    public async Task<IActionResult> Unfollow(string slug)
    {
        var creator = await _creators.GetByUsernameAsync(slug);
        if (creator is null) return NotFoundResponse("Creator not found.");
        if (!Guid.TryParse(creator.Id, out var creatorGuid)) return ErrorResponse("Invalid creator ID.");

        var userId = GetRequiredUserId()!;
        await _creators.UnfollowAsync(userId, creatorGuid);

        var followerCount = await _creators.GetFollowerCountAsync(creatorGuid);
        return OkResponse(new { following = false, followerCount });
    }

    [Authorize]
    [HttpGet("{slug}/follow")]
    public async Task<IActionResult> GetFollowStatus(string slug)
    {
        var creator = await _creators.GetByUsernameAsync(slug);
        if (creator is null) return NotFoundResponse("Creator not found.");
        if (!Guid.TryParse(creator.Id, out var creatorGuid)) return ErrorResponse("Invalid creator ID.");

        var userId = GetRequiredUserId()!;
        var following = await _creators.IsFollowingAsync(userId, creatorGuid);
        var followerCount = await _creators.GetFollowerCountAsync(creatorGuid);
        return OkResponse(new { following, followerCount });
    }

    // ───── Helpers ─────

    // ───── Magic byte signatures for image validation ─────

    private static readonly Dictionary<string, byte[][]> ImageMagicBytes = new()
    {
        [".jpg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
        [".jpeg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
        [".png"] = [new byte[] { 0x89, 0x50, 0x4E, 0x47 }],
        [".webp"] = [System.Text.Encoding.ASCII.GetBytes("RIFF")],
    };

    /// <summary>
    /// Validates and uploads an image. Returns the public URL on success, null for validation
    /// failures, or throws on storage errors (caller should catch and return 502).
    /// </summary>
    private async Task<string?> UploadImage(IFormFile? file, string folder)
    {
        if (file is null || file.Length == 0) return null;
        if (file.Length > MaxImageSize) return null;

        var ext = Path.GetExtension(file.FileName)?.ToLowerInvariant() ?? "";
        if (!AllowedImageExtensions.Contains(ext)) return null;

        var key = $"{folder}/{Guid.NewGuid()}{ext}";
        await using var stream = file.OpenReadStream();

        // Validate magic bytes to prevent disguised file uploads
        if (ImageMagicBytes.TryGetValue(ext, out var signatures))
        {
            var header = new byte[12];
            var bytesRead = await stream.ReadAsync(header.AsMemory(0, 12));
            stream.Position = 0;
            if (bytesRead < 3) return null;
            var matched = false;
            foreach (var magic in signatures)
            {
                if (bytesRead >= magic.Length && header.AsSpan(0, magic.Length).SequenceEqual(magic))
                { matched = true; break; }
            }
            if (!matched) return null;
        }

        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
        await _storage.UploadAsync(stream, key, contentType);
        return _storage.GetPublicUrl(key);
    }

    private void ResolveCollectionImageUrls(IEnumerable<TrackCollectionDto> collections)
    {
        foreach (var collection in collections)
            ResolveCollectionImageUrl(collection);
    }

    private void ResolveCollectionImageUrl(TrackCollectionDto collection)
    {
        if (!string.IsNullOrWhiteSpace(collection.CoverImageUrl))
            collection.CoverImageUrl = ResolveImageUrl(collection.CoverImageUrl);
    }

    /// <summary>Null = keep stored value; "public"/"hidden" pass through; anything else is invalid.</summary>
    private static bool TryParseCollectionVisibility(string? raw, out string? visibility)
    {
        visibility = null;
        if (raw is null) return true;
        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized is not ("public" or "hidden")) return false;
        visibility = normalized;
        return true;
    }

    /// <summary>
    /// Null = keep stored value; empty string = clear; otherwise must parse as a
    /// date. Parsed values are coerced to UTC (Npgsql timestamptz requirement).
    /// </summary>
    private static bool TryParseReleaseDate(string? raw, out DateTime? releaseDate, out bool clear)
    {
        releaseDate = null;
        clear = false;
        if (raw is null) return true;
        if (string.IsNullOrWhiteSpace(raw))
        {
            clear = true;
            return true;
        }
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        releaseDate = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }
}
