using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class UploadService : IUploadService
{
    private readonly IObjectStorage _storage;
    private readonly ITrackRepository _tracks;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<UploadService> _logger;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ICreatorProfileRepository? _profiles;

    /// <summary>Allowed audio file extensions (lowercase, with dot).</summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a"
    };

    /// <summary>Allowed MIME types for audio uploads.</summary>
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mpeg", "audio/mp3", "audio/wav", "audio/x-wav", "audio/wave",
        "audio/vnd.wave", "audio/x-pn-wav",
        "audio/flac", "audio/x-flac", "audio/aac", "audio/mp4", "audio/m4a",
        "audio/ogg", "audio/x-m4a"
    };

    /// <summary>Allowed image extensions for cover art.</summary>
    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    /// <summary>Allowed MIME types for cover art.</summary>
    private static readonly HashSet<string> AllowedImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };

    /// <summary>Maximum upload size in bytes (default 100 MB).</summary>
    private const long MaxFileSizeBytes = 100 * 1024 * 1024;

    /// <summary>Maximum cover art size in bytes (10 MB).</summary>
    private const long MaxCoverArtSizeBytes = 10 * 1024 * 1024;

    /// <summary>Magic byte signatures for audio file validation.</summary>
    private static readonly Dictionary<string, byte[][]> AudioMagicBytes = new()
    {
        [".mp3"] = [new byte[] { 0xFF, 0xFB }, new byte[] { 0xFF, 0xF3 }, new byte[] { 0xFF, 0xF2 }, "ID3"u8.ToArray()],
        // WAV variants: standard RIFF, RF64 (64-bit extension), BW64 (Broadcast Wave 64).
        // All valid WAV containers also carry "WAVE" at offset 8, checked as a secondary gate.
        [".wav"] = ["RIFF"u8.ToArray(), "RF64"u8.ToArray(), "BW64"u8.ToArray()],
        [".flac"] = ["fLaC"u8.ToArray()],
        [".ogg"] = ["OggS"u8.ToArray()],
        [".aac"] = [new byte[] { 0xFF, 0xF1 }, new byte[] { 0xFF, 0xF9 }],
        // M4A/MP4 containers: "ftyp" appears at byte offset 4 (first 4 bytes are box size)
        [".m4a"] = [],
    };

    /// <summary>Magic byte signatures for image file validation.</summary>
    private static readonly Dictionary<string, byte[][]> ImageMagicBytes = new()
    {
        [".jpg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
        [".jpeg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
        [".png"] = [new byte[] { 0x89, 0x50, 0x4E, 0x47 }],
        [".webp"] = ["RIFF"u8.ToArray()],
    };

    private bool ValidateMagicBytes(Stream stream, string extension, Dictionary<string, byte[][]> signatures)
    {
        if (!signatures.TryGetValue(extension, out var expected))
            return false;

        // Always read from the very start, regardless of current stream position.
        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);

        var buffer = new byte[12];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);

        // Reset so the caller can pipe the full file to storage.
        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);

        if (bytesRead < 2)
        {
            _logger.LogWarning("MagicBytes: only {N} bytes readable for ext={Ext}", bytesRead, extension);
            return false;
        }

        // M4A/MP4 special case: "ftyp" appears at offset 4 (first 4 bytes are box size)
        if (extension is ".m4a" && bytesRead >= 8
            && buffer.AsSpan(4, 4).SequenceEqual("ftyp"u8))
            return true;

        // Primary check: match any known magic byte sequence at offset 0
        foreach (var magic in expected)
        {
            if (magic.Length > 0 && bytesRead >= magic.Length && buffer.AsSpan(0, magic.Length).SequenceEqual(magic))
                return true;
        }

        // WAV fallback: all valid WAV containers embed "WAVE" at offset 8 regardless of
        // the outer chunk identifier (RIFF, RF64, BW64, or future variants).
        if (extension is ".wav" && bytesRead >= 12
            && buffer.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return true;

        // Log first 12 bytes as hex to diagnose unexpected file formats in production.
        _logger.LogWarning("MagicBytes FAIL ext={Ext} bytes=[{Hex}] text=[{Text}]",
            extension,
            Convert.ToHexString(buffer.AsSpan(0, bytesRead)),
            System.Text.Encoding.ASCII.GetString(buffer.AsSpan(0, bytesRead)).Replace('\0', '.'));

        return false;
    }

    public UploadService(
        IObjectStorage storage,
        ITrackRepository tracks,
        UserManager<ApplicationUser> users,
        ILogger<UploadService> logger,
        ICreatorIdentityRepository creators,
        ICreatorProfileRepository? profiles = null)
    {
        _storage = storage;
        _tracks = tracks;
        _users = users;
        _logger = logger;
        _creators = creators;
        _profiles = profiles;
    }

    public async Task<UploadTrackResponse> Upload(UploadTrackRequest request)
    {
        if (request.Audio is null || request.Audio.Length == 0)
            throw new ArgumentException("Audio file is required.");

        if (string.IsNullOrWhiteSpace(request.CreatorId))
            throw new ArgumentException("CreatorId is required.");

        // ── Tier-based upload limit enforcement ──
        var creator = await _users.FindByIdAsync(request.CreatorId);
        if (creator is null)
            throw new ArgumentException("Creator not found.");

        var tierConfig = TierManifest.For(creator.CreatorTier);
        if (tierConfig.UploadLimit.HasValue && creator.UploadCount >= tierConfig.UploadLimit.Value)
        {
            _logger.LogWarning("Upload denied: creator {CreatorId} has {Count}/{Limit} tracks (tier={Tier})",
                request.CreatorId, creator.UploadCount, tierConfig.UploadLimit.Value, tierConfig.Slug);
            throw new InvalidOperationException(
                $"Upload limit reached. {tierConfig.DisplayName} tier allows {tierConfig.UploadLimit} tracks. Upgrade to Pro for unlimited uploads.");
        }

        // ── File-type validation ──
        var extension = Path.GetExtension(request.Audio.FileName)?.ToLowerInvariant() ?? "";
        if (!AllowedExtensions.Contains(extension))
            throw new ArgumentException(
                $"File type '{extension}' is not allowed. Accepted: {string.Join(", ", AllowedExtensions)}");

        // SECURITY: Require Content-Type to be present AND in the allowed set (no empty bypass)
        var contentType = request.Audio.ContentType?.ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(contentType) || !AllowedMimeTypes.Contains(contentType))
            throw new ArgumentException(
                $"MIME type '{contentType}' is not allowed for audio uploads.");

        // ── File-size validation ──
        if (request.Audio.Length > MaxFileSizeBytes)
            throw new ArgumentException(
                $"File size ({request.Audio.Length / (1024 * 1024)} MB) exceeds the {MaxFileSizeBytes / (1024 * 1024)} MB limit.");

        // Sanitize filename — strip path traversal characters, use a unique key
        var safeFileName = Path.GetFileNameWithoutExtension(request.Audio.FileName)
            .Replace("..", "")
            .Replace("/", "")
            .Replace("\\", "");
        var key = $"tracks/{request.CreatorId}/{Guid.NewGuid()}{extension}";

        await using var stream = request.Audio.OpenReadStream();

        // SECURITY: Validate magic bytes to prevent disguised file uploads
        if (!ValidateMagicBytes(stream, extension, AudioMagicBytes))
            throw new ArgumentException(
                $"File content does not match expected format for '{extension}'. The file may be corrupted or disguised.");
        var audioUrl = await _storage.UploadAsync(
            stream,
            key,
            string.IsNullOrWhiteSpace(request.Audio.ContentType) ? "audio/mpeg" : request.Audio.ContentType);

        string? coverArtUrl = null;
        if (request.CoverArt is not null && request.CoverArt.Length > 0)
            coverArtUrl = await UploadCoverArtAsync(request.CreatorId!, request.CoverArt);

        // Derive cents from the dedicated price fields when provided,
        // otherwise fall back to the generic Price so tracks uploaded via the
        // single-price form still display correctly on the marketplace.
        var priceCents = request.Price.HasValue
            ? (int)Math.Round(request.Price.Value * 100, MidpointRounding.AwayFromZero)
            : 0;

        // Resolve the Creator UUID for the uploading user
        var creatorUuid = await _creators.GetCreatorIdForUserAsync(request.CreatorId!);

        var track = new Track
        {
            Id = Guid.NewGuid(),
            CambrianTrackId = TrackIdDto.Generate(),
            Title = request.Title,
            Description = request.Description,
            Price = request.Price ?? 0,
            LicenseType = NormalizeListingLicenseType(request.LicenseType),
            AudioUrl = audioUrl,
            CoverArtUrl = coverArtUrl,
            NonExclusivePriceCents = request.NonExclusivePrice.HasValue
                ? (int)Math.Round(request.NonExclusivePrice.Value * 100, MidpointRounding.AwayFromZero)
                : priceCents,
            ExclusivePriceCents = request.ExclusivePrice.HasValue
                ? (int)Math.Round(request.ExclusivePrice.Value * 100, MidpointRounding.AwayFromZero)
                : priceCents,
            CopyrightBuyoutPriceCents = request.CopyrightBuyoutPrice.HasValue
                ? (int)Math.Round(request.CopyrightBuyoutPrice.Value * 100, MidpointRounding.AwayFromZero)
                : 0,
            CreatorId = request.CreatorId,
            CreatorUuid = creatorUuid,
            Tags = string.IsNullOrWhiteSpace(request.Tags)
                ? new List<string>()
                : request.Tags
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
        };
        ApplyGenreFields(track, request.PrimaryGenre, request.Subgenre, request.Genre);

        await _tracks.AddAsync(track);

        var linkedCollection = await AssignTrackToCollectionAsync(request, track);

        // ── Increment upload count ──
        creator.UploadCount += 1;
        await _users.UpdateAsync(creator);

        _logger.LogInformation("Track uploaded: {TrackId} by creator {CreatorId} (upload #{Count}, tier={Tier})",
            track.Id, request.CreatorId, creator.UploadCount, tierConfig.Slug);

        return new UploadTrackResponse
        {
            TrackId = track.Id.ToString(),
            Title = track.Title,
            CambrianTrackId = track.CambrianTrackId ?? "",
            Genre = GetCanonicalGenre(track),
            PrimaryGenre = track.PrimaryGenre,
            Subgenre = track.Subgenre,
            CoverArtUrl = track.CoverArtUrl,
            CollectionId = linkedCollection?.Id,
            CollectionTitle = linkedCollection?.Title,
            CollectionTrackIds = linkedCollection?.TrackIds ?? Array.Empty<string>(),
        };
    }

    public async Task<string> UploadCoverArtAsync(string creatorId, IFormFile coverArt)
    {
        if (string.IsNullOrWhiteSpace(creatorId))
            throw new ArgumentException("CreatorId is required.");

        if (coverArt is null || coverArt.Length <= 0)
            throw new ArgumentException("Cover art file is required.");

        if (coverArt.Length > MaxCoverArtSizeBytes)
            throw new ArgumentException(
                $"Cover art size ({coverArt.Length / (1024 * 1024)} MB) exceeds the {MaxCoverArtSizeBytes / (1024 * 1024)} MB limit.");

        var imgExt = Path.GetExtension(coverArt.FileName)?.ToLowerInvariant() ?? "";
        if (!AllowedImageExtensions.Contains(imgExt))
            throw new ArgumentException(
                $"Cover art type '{imgExt}' is not allowed. Accepted: {string.Join(", ", AllowedImageExtensions)}");

        var imgMime = coverArt.ContentType?.ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(imgMime) || !AllowedImageMimeTypes.Contains(imgMime))
            throw new ArgumentException(
                $"Cover art MIME type '{imgMime}' is not allowed.");

        var coverKey = $"covers/{creatorId}/{Guid.NewGuid()}{imgExt}";
        await using var coverStream = coverArt.OpenReadStream();

        if (!ValidateMagicBytes(coverStream, imgExt, ImageMagicBytes))
            throw new ArgumentException(
                $"Cover art content does not match expected format for '{imgExt}'. The file may be corrupted or disguised.");

        return await _storage.UploadAsync(
            coverStream,
            coverKey,
            string.IsNullOrWhiteSpace(imgMime) ? "image/jpeg" : imgMime);
    }

    private async Task<TrackCollectionDto?> AssignTrackToCollectionAsync(UploadTrackRequest request, Track track)
    {
        if (string.IsNullOrWhiteSpace(request.AlbumAssignmentType))
            return null;

        if (_profiles is null)
            throw new InvalidOperationException("Album assignment requires creator profile storage.");

        var assignmentType = request.AlbumAssignmentType.Trim().ToLowerInvariant();
        var trackId = track.Id.ToString();

        if (assignmentType == "existing")
        {
            if (!request.CollectionId.HasValue)
                throw new ArgumentException("CollectionId is required when AlbumAssignmentType is 'existing'.");

            var owner = await _profiles.GetCollectionOwnerAsync(request.CollectionId.Value);
            if (!string.Equals(owner, request.CreatorId, StringComparison.Ordinal))
                throw new InvalidOperationException("You can only add tracks to your own album.");

            var existing = await _profiles.GetCollectionByIdAsync(request.CollectionId.Value)
                ?? throw new InvalidOperationException("Selected album was not found.");

            return await _profiles.UpdateCollectionAsync(
                request.CollectionId.Value,
                request.CreatorId!,
                null,
                null,
                null,
                AppendTrackId(existing.TrackIds, trackId));
        }

        if (assignmentType == "new")
        {
            if (string.IsNullOrWhiteSpace(request.NewAlbumTitle))
                throw new ArgumentException("NewAlbumTitle is required when AlbumAssignmentType is 'new'.");

            return await _profiles.AddCollectionAsync(
                request.CreatorId!,
                request.NewAlbumTitle.Trim(),
                NormalizeNullableText(request.NewAlbumDescription),
                null,
                trackId);
        }

        return null;
    }

    private static void ApplyGenreFields(Track track, string? primaryGenre, string? subgenre, string? legacyGenre)
    {
        var normalizedPrimary = NormalizeNullableText(primaryGenre);
        var normalizedSubgenre = NormalizeNullableText(subgenre);
        var normalizedLegacy = NormalizeNullableText(legacyGenre);

        track.PrimaryGenre = normalizedPrimary;
        track.Subgenre = normalizedSubgenre ?? normalizedLegacy;
        track.Genre = track.Subgenre ?? track.PrimaryGenre ?? normalizedLegacy;
    }

    private static string? NormalizeNullableText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Track.LicenseType is the *listing* tier, not the buyer's usage. The frontend
    // upload form historically sent usage values like "personal", which the
    // marketplace UI then read as "not for sale". Only accept the three canonical
    // tiers; anything else (including "personal", "streaming", null) collapses to
    // "non-exclusive" so the track is sellable by default.
    private static string NormalizeListingLicenseType(string? requested) =>
        requested switch
        {
            "non-exclusive" or "exclusive" or "copyright_buyout" => requested,
            _ => "non-exclusive"
        };

    private static string GetCanonicalGenre(Track track) =>
        track.Subgenre ?? track.Genre ?? track.PrimaryGenre ?? string.Empty;

    private static string AppendTrackId(IEnumerable<string> existingTrackIds, string trackId)
    {
        var orderedIds = existingTrackIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        if (!orderedIds.Contains(trackId, StringComparer.OrdinalIgnoreCase))
            orderedIds.Add(trackId);

        return string.Join(",", orderedIds);
    }
}
