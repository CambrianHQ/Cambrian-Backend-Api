using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class CatalogService : ICatalogService
{
    private readonly ITrackRepository _tracks;

    public CatalogService(ITrackRepository tracks)
    {
        _tracks = tracks;
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync(int page = 1, int pageSize = 50, string? genre = null, string? search = null, string? sort = null)
    {
        var tracks = await _tracks.BrowseAsync(page, pageSize, genre, search, sort);

        return tracks.Select(t => MapToResponse(t)).ToList();
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync(int page = 1, int pageSize = 20, string? genre = null, string? search = null)
    {
        return await GetCatalogAsync(page, pageSize, genre, search);
    }

    public async Task<TrackResponse?> GetTrackAsync(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            return null;

        var track = await _tracks.GetByIdAsync(id);

        return track is null ? null : MapToResponse(track);
    }

    private const decimal PlatformFeeRate = 0.15m;

    private static TrackResponse MapToResponse(Track t)
    {
        var nonExPrice = t.NonExclusivePriceCents / 100m;
        var exPrice = t.ExclusivePriceCents / 100m;

        return new TrackResponse
        {
            Id = t.Id.ToString(),
            Title = t.Title,
            Description = t.Description,
            Genre = t.Genre ?? "",
            Price = (decimal)t.Price,
            NonExclusivePrice = nonExPrice,
            ExclusivePrice = exPrice,
            PlatformFeePercent = PlatformFeeRate,
            NonExclusivePlatformFee = Math.Round(nonExPrice * PlatformFeeRate, 2),
            NonExclusiveCreatorEarnings = Math.Round(nonExPrice * (1 - PlatformFeeRate), 2),
            ExclusivePlatformFee = Math.Round(exPrice * PlatformFeeRate, 2),
            ExclusiveCreatorEarnings = Math.Round(exPrice * (1 - PlatformFeeRate), 2),
            ExclusiveSold = t.ExclusiveSold,
            LicenseType = t.LicenseType,
            Duration = t.Duration,
            AudioUrl = t.AudioUrl,
            CoverArtUrl = t.CoverArtUrl,
            CreatorId = t.CreatorId,
            Artist = t.Creator?.DisplayName ?? t.Creator?.Email,
            CreatedAt = t.CreatedAt,
        };
    }
}