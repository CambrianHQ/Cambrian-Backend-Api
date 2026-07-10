namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiTrackSearchResult
{
    public string TrackId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    public AiCreatorSummary Creator { get; set; } = new();

    public double Score { get; set; }
    public string FitConfidence { get; set; } = "medium"; // low | medium | high

    public string Reason { get; set; } = string.Empty;
    public string BestUseCase { get; set; } = string.Empty;
    public List<string> SecondaryUseCases { get; set; } = new();

    public string WhyThisWorks { get; set; } = string.Empty;

    public AiTrackAttributes Attributes { get; set; } = new();
    public AiLicenseSummary License { get; set; } = new();
    public AiTrackPreview Preview { get; set; } = new();
}
