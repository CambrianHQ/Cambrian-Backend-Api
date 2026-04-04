namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiTrackSearchResultDto
{
    public string TrackId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    public AiCreatorSummaryDto Creator { get; set; } = new();

    public double Score { get; set; }
    public string FitConfidence { get; set; } = "medium"; // low | medium | high

    public string Reason { get; set; } = string.Empty;
    public string BestUseCase { get; set; } = string.Empty;
    public List<string> SecondaryUseCases { get; set; } = new();

    public string WhyThisWorks { get; set; } = string.Empty;

    public AiTrackAttributesDto Attributes { get; set; } = new();
    public AiLicenseSummaryDto License { get; set; } = new();
    public AiTrackPreviewDto Preview { get; set; } = new();
}
