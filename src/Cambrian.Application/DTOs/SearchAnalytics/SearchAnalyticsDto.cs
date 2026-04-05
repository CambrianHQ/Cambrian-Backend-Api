namespace Cambrian.Application.DTOs.SearchAnalytics;

public class TrendingSearchDto
{
    public string Query { get; set; } = null!;
    public int Count { get; set; }
}

public class TrendingSearchesResponse
{
    public List<TrendingSearchDto> TrendingSearches { get; set; } = new();
    public List<TrendingSearchDto> ZeroResultQueries { get; set; } = new();
}
