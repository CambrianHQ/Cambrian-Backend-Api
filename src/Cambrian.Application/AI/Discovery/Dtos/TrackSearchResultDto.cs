namespace Cambrian.Application.AI.Discovery.Dtos;

/// <summary>
/// Canonical AI-optimised track search result. Used by both the REST API and MCP adapter.
/// Every field is pre-computed at query time so consumers never need to post-process.
/// </summary>
public class TrackSearchResultDto
{
    public string TrackId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    public CreatorSummaryDto Creator { get; set; } = new();

    /// <summary>Composite relevance score (0–1). Higher = better match.</summary>
    public double Score { get; set; }

    /// <summary>Human-readable confidence label: "high", "medium", "low".</summary>
    public string FitConfidence { get; set; } = "medium";

    /// <summary>One-sentence explanation of why this track matched the query.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Primary recommended use case (e.g. "vlog", "podcast intro").</summary>
    public string BestUseCase { get; set; } = string.Empty;

    /// <summary>Other viable use cases for this track.</summary>
    public List<string> SecondaryUseCases { get; set; } = new();

    /// <summary>AI-generated persuasive blurb explaining the track's fit.</summary>
    public string WhyThisWorks { get; set; } = string.Empty;

    public TrackAttributesDto Attributes { get; set; } = new();
    public LicenseSummaryDto License { get; set; } = new();
    public TrackPreviewDto Preview { get; set; } = new();
}
