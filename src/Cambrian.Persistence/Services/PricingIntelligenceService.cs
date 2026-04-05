using Cambrian.Application.DTOs.Pricing;
using Cambrian.Application.Interfaces;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Services;

public class PricingIntelligenceService : IPricingIntelligenceService
{
    private readonly CambrianDbContext _db;

    public PricingIntelligenceService(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<PricingIntelligenceDto?> GetGenrePricingAsync(string genre)
    {
        var tracks = await _db.Tracks
            .Where(t => t.Genre != null
                && t.Genre.ToLower() == genre.ToLower()
                && t.Status == "available"
                && t.Visibility == "public")
            .Select(t => t.NonExclusivePriceCents)
            .ToListAsync();

        if (tracks.Count == 0) return null;

        return BuildPricingDto(genre, tracks);
    }

    public async Task<List<CreatorPricingPositionDto>> GetCreatorPositionAsync(string creatorUserId)
    {
        var creatorTracks = await _db.Tracks
            .Where(t => t.CreatorId == creatorUserId && t.Status == "available" && t.Visibility == "public")
            .Select(t => new { t.Genre, t.NonExclusivePriceCents })
            .ToListAsync();

        var genres = creatorTracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Genre))
            .Select(t => t.Genre!.ToLower())
            .Distinct()
            .ToList();

        var result = new List<CreatorPricingPositionDto>();

        foreach (var genre in genres)
        {
            var allPrices = await _db.Tracks
                .Where(t => t.Genre != null
                    && t.Genre.ToLower() == genre
                    && t.Status == "available"
                    && t.Visibility == "public")
                .Select(t => t.NonExclusivePriceCents)
                .ToListAsync();

            if (allPrices.Count == 0) continue;

            var creatorPrices = creatorTracks
                .Where(t => t.Genre != null && t.Genre.ToLower() == genre)
                .Select(t => t.NonExclusivePriceCents)
                .ToList();

            var creatorAvg = creatorPrices.Count > 0 ? (int)creatorPrices.Average() : 0;

            var pricingBase = BuildPricingDto(genre, allPrices);
            var belowCount = allPrices.Count(p => p < creatorAvg);
            var percentile = allPrices.Count > 0 ? Math.Round(belowCount * 100.0 / allPrices.Count, 1) : 0;

            result.Add(new CreatorPricingPositionDto
            {
                Genre = genre,
                AveragePriceCents = pricingBase.AveragePriceCents,
                MedianPriceCents = pricingBase.MedianPriceCents,
                MinPriceCents = pricingBase.MinPriceCents,
                MaxPriceCents = pricingBase.MaxPriceCents,
                Distribution = pricingBase.Distribution,
                TotalTracksInGenre = pricingBase.TotalTracksInGenre,
                CreatorAveragePriceCents = creatorAvg,
                PercentileRank = percentile
            });
        }

        return result;
    }

    private static PricingIntelligenceDto BuildPricingDto(string genre, List<int> prices)
    {
        prices.Sort();
        var avg = (int)prices.Average();
        var median = prices[prices.Count / 2];
        var min = prices[0];
        var max = prices[^1];

        return new PricingIntelligenceDto
        {
            Genre = genre,
            AveragePriceCents = avg,
            MedianPriceCents = median,
            MinPriceCents = min,
            MaxPriceCents = max,
            TotalTracksInGenre = prices.Count,
            Distribution = new PriceDistributionDto
            {
                Bucket0To10 = prices.Count(p => p < 1000),
                Bucket10To25 = prices.Count(p => p >= 1000 && p < 2500),
                Bucket25To50 = prices.Count(p => p >= 2500 && p < 5000),
                Bucket50To100 = prices.Count(p => p >= 5000 && p < 10000),
                Bucket100Plus = prices.Count(p => p >= 10000)
            }
        };
    }
}
