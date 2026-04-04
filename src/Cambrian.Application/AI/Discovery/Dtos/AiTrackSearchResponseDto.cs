namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiTrackSearchResponseDto
{
    public List<AiTrackSearchResultDto> Results { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public AiQuerySummaryDto QuerySummary { get; set; } = new();
}
