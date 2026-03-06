using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class UploadService : IUploadService
{
    private readonly IObjectStorage _storage;
    private readonly ITrackRepository _tracks;

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

        var extension = Path.GetExtension(request.Audio.FileName);
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