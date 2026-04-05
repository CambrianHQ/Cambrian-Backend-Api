using Cambrian.Application.DTOs.SearchAnalytics;

namespace Cambrian.Application.Interfaces;

public interface ISearchAnalyticsService
{
    Task LogSearchAsync(string query, string? filters, int resultCount, string? userId, string? sessionId);
    Task<TrendingSearchesResponse> GetTrendingSearchesAsync(int days = 7);
}
