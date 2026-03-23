using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class UploadService : IUploadService
{
    private readonly IObjectStorage _storage;
    private readonly ITrackRepository _tracks;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<UploadService> _logger;

    /// <summary>Allowed audio file extensions (lowercase, with dot).</summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a"
    };

    /// <summary>Allowed MIME types for audio uploads.</summary>
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mpeg", "audio/mp3", "audio/wav", "audio/x-wav", "audio/wave",
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
        [".wav"] = ["RIFF"u8.ToArray()],
        [".flac"] = ["fLaC"u8.ToArray()],
        [".ogg"] = ["OggS"u8.ToArray()],
        [".aac"] = [new byte[] { 0xFF, 0xF1 }, new byte[] { 0xFF, 0xF9 }],
        [".m4a"] = [new byte[] { 0x00, 0x00, 0x00 }], // ftyp box (variable offset)
    };

    /// <summary>Magic byte signatures for image file validation.</summary>
    private static readonly Dictionary<string, byte[][]> ImageMagicBytes = new()
    {
        [".jpg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
        [".jpeg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
        [".png"] = [new byte[] { 0x89, 0x50, 0x4E, 0x47 }],
        [".webp"] = ["RIFF"u8.ToArray()],
    };

    private static bool ValidateMagicBytes(Stream stream, string extension, Dictionary<string, byte[][]> signatures)
    {
        if (!signatures.TryGetValue(extension, out var expected))
            return false;

        var buffer = new byte[12];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);
        stream.Position = 0; // reset for upload

        if (bytesRead < 2)
            return false;

        foreach (var magic in expected)
        {
            if (bytesRead >= magic.Length && buffer.AsSpan(0, magic.Length).SequenceEqual(magic))
                return true;
        }

        return false;
    }

    public UploadService(IObjectStorage storage, ITrackRepository tracks, UserManager<ApplicationUser> users, ILogger<UploadService> logger)
    {
        _storage = storage;
        _tracks = tracks;
        _users = users;
        _logger = logger;
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

        // ── Optional cover art upload ──
        string? coverArtUrl = null;
        if (request.CoverArt is not null && request.CoverArt.Length > 0)
        {
            if (request.CoverArt.Length > MaxCoverArtSizeBytes)
                throw new ArgumentException(
                    $"Cover art size ({request.CoverArt.Length / (1024 * 1024)} MB) exceeds the {MaxCoverArtSizeBytes / (1024 * 1024)} MB limit.");

            var imgExt = Path.GetExtension(request.CoverArt.FileName)?.ToLowerInvariant() ?? "";
            if (!AllowedImageExtensions.Contains(imgExt))
                throw new ArgumentException(
                    $"Cover art type '{imgExt}' is not allowed. Accepted: {string.Join(", ", AllowedImageExtensions)}");

            // SECURITY: Require Content-Type for images (no empty bypass)
            var imgMime = request.CoverArt.ContentType?.ToLowerInvariant() ?? "";
            if (string.IsNullOrEmpty(imgMime) || !AllowedImageMimeTypes.Contains(imgMime))
                throw new ArgumentException(
                    $"Cover art MIME type '{imgMime}' is not allowed.");

            var coverKey = $"covers/{request.CreatorId}/{Guid.NewGuid()}{imgExt}";
            await using var coverStream = request.CoverArt.OpenReadStream();

            // SECURITY: Validate image magic bytes
            if (!ValidateMagicBytes(coverStream, imgExt, ImageMagicBytes))
                throw new ArgumentException(
                    $"Cover art content does not match expected format for '{imgExt}'. The file may be corrupted or disguised.");
            coverArtUrl = await _storage.UploadAsync(
                coverStream,
                coverKey,
                string.IsNullOrWhiteSpace(imgMime) ? "image/jpeg" : imgMime);
        }

        // Derive cents from the dedicated price fields when provided,
        // otherwise fall back to the generic Price so tracks uploaded via the
        // single-price form still display correctly on the marketplace.
        var priceCents = request.Price.HasValue
            ? (int)Math.Round(request.Price.Value * 100, MidpointRounding.AwayFromZero)
            : 0;

        var track = new Track
        {
            Id = Guid.NewGuid(),
            CambrianTrackId = TrackIdDto.Generate(),
            Title = request.Title,
            Description = request.Description,
            Genre = request.Genre,
            Price = request.Price ?? 0,
            LicenseType = request.LicenseType ?? "streaming",
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
            Tags = string.IsNullOrWhiteSpace(request.Tags)
                ? new List<string>()
                : request.Tags
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
        };

        await _tracks.AddAsync(track);

        // ── Increment upload count ──
        creator.UploadCount += 1;
        await _users.UpdateAsync(creator);

        _logger.LogInformation("Track uploaded: {TrackId} by creator {CreatorId} (upload #{Count}, tier={Tier})",
            track.Id, request.CreatorId, creator.UploadCount, tierConfig.Slug);

        return new UploadTrackResponse
        {
            TrackId = track.Id.ToString(),
            Title = track.Title,
            CambrianTrackId = track.CambrianTrackId ?? ""
        };
    }
}
