using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class UploadService : IUploadService
{
    private readonly IObjectStorage _storage;
    private readonly ITrackRepository _tracks;

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
        "audio/ogg", "audio/x-m4a", "application/octet-stream"
    };

    /// <summary>Maximum upload size in bytes (default 100 MB).</summary>
    private const long MaxFileSizeBytes = 100 * 1024 * 1024;

    public UploadService(IObjectStorage storage, ITrackRepository tracks)
    {
        _storage = storage;
        _tracks = tracks;
    }

    public async Task<string> Upload(UploadTrackRequest request)
    {
        if (request.Audio is null || request.Audio.Length == 0)
            throw new ArgumentException("Audio file is required.");

        if (string.IsNullOrWhiteSpace(request.CreatorId))
            throw new ArgumentException("CreatorId is required.");

        // ── File-type validation ──
        var extension = Path.GetExtension(request.Audio.FileName)?.ToLowerInvariant() ?? "";
        if (!AllowedExtensions.Contains(extension))
            throw new ArgumentException(
                $"File type '{extension}' is not allowed. Accepted: {string.Join(", ", AllowedExtensions)}");

        var contentType = request.Audio.ContentType?.ToLowerInvariant() ?? "";
        if (!string.IsNullOrEmpty(contentType) && !AllowedMimeTypes.Contains(contentType))
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
        var audioUrl = await _storage.UploadAsync(
            stream,
            key,
            string.IsNullOrWhiteSpace(request.Audio.ContentType) ? "audio/mpeg" : request.Audio.ContentType);

        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = request.Title,
            Description = request.Description,
            Genre = request.Genre,
            Price = request.Price ?? 0,
            LicenseType = request.LicenseType ?? "streaming",
            AudioUrl = audioUrl,
            NonExclusivePriceCents = request.NonExclusivePrice.HasValue
                ? (int)Math.Round(request.NonExclusivePrice.Value * 100, MidpointRounding.AwayFromZero)
                : 0,
            ExclusivePriceCents = request.ExclusivePrice.HasValue
                ? (int)Math.Round(request.ExclusivePrice.Value * 100, MidpointRounding.AwayFromZero)
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

        return track.Id.ToString();
    }
}