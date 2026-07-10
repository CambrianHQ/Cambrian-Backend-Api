namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiTrackDetails
{
    public string TrackId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public AiCreatorSummary Creator { get; set; } = new();

    public AiTrackAttributes Attributes { get; set; } = new();

    public List<string> Tags { get; set; } = new();
    public string? AiGenerator { get; set; }

    public List<string> UseCaseHints { get; set; } = new();

    public string WhyThisWorks { get; set; } = string.Empty;

    public AiLicenseSummary LicenseSummary { get; set; } = new();
    public AiTrackPreview Preview { get; set; } = new();

    public string? WaveformImageUrl { get; set; }
    public string? CoverImageUrl { get; set; }
}
