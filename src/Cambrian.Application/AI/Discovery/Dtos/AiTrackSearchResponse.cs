namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiTrackSearchResponse
{
    public List<AiTrackSearchResult> Results { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public AiQuerySummary QuerySummary { get; set; } = new();
}
