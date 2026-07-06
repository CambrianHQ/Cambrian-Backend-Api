namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiQuerySummary
{
    public string Intent { get; set; } = string.Empty;
    public List<string> MatchedOn { get; set; } = new();
    public string? Notes { get; set; }
}
