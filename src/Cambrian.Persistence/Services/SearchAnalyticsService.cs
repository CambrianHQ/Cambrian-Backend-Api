using Cambrian.Application.DTOs.SearchAnalytics;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Services;

public class SearchAnalyticsService : ISearchAnalyticsService
{
    private readonly CambrianDbContext _db;

    public SearchAnalyticsService(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task LogSearchAsync(string query, string? filters, int resultCount, string? userId, string? sessionId)
    {
        var searchQuery = new SearchQuery
        {
            Id = Guid.NewGuid(),
            Query = query,
            Filters = filters,
            ResultCount = resultCount,
            UserId = userId,
            SessionId = sessionId,
            CreatedAt = DateTime.UtcNow
        };

        _db.SearchQueries.Add(searchQuery);
        await _db.SaveChangesAsync();
    }

    public async Task<TrendingSearchesResponse> GetTrendingSearchesAsync(int days = 7)
    {
        var since = DateTime.UtcNow.AddDays(-days);

        var trending = await _db.SearchQueries
            .Where(s => s.CreatedAt >= since)
            .GroupBy(s => s.Query.ToLower())
            .Select(g => new TrendingSearchDto
            {
                Query = g.Key,
                Count = g.Count()
            })
            .OrderByDescending(t => t.Count)
            .Take(20)
            .ToListAsync();

        var zeroResults = await _db.SearchQueries
            .Where(s => s.CreatedAt >= since && s.ResultCount == 0)
            .GroupBy(s => s.Query.ToLower())
            .Select(g => new TrendingSearchDto
            {
                Query = g.Key,
                Count = g.Count()
            })
            .OrderByDescending(t => t.Count)
            .Take(20)
            .ToListAsync();

        return new TrendingSearchesResponse
        {
            TrendingSearches = trending,
            ZeroResultQueries = zeroResults
        };
    }
}
