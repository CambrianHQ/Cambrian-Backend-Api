using System.ComponentModel.DataAnnotations;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Public "Behind The Track" payload — the creator's account of how a track
/// was made (creation story, production notes, optional process video, tools used).
/// </summary>
public class BehindTheTrackDto
{
    public string TrackId { get; set; } = "";
    public string? Story { get; set; }
    public string? DAW { get; set; }
    public string? VocalChain { get; set; }
    public string? PromptNotes { get; set; }
    public string? ProductionNotes { get; set; }
    public string? HumanContributionNotes { get; set; }
    public string? YoutubeUrl { get; set; }
    public IReadOnlyList<string> ToolsUsed { get; set; } = Array.Empty<string>();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Populated only by the v1 "behind-the-track" view endpoint. Legacy callers
    /// (catalog/creator routes) leave this empty — proof videos are managed and
    /// read exclusively through the v1 proof-videos endpoints.
    /// </summary>
    public IReadOnlyList<TrackVideoProofDto> ProofVideos { get; set; } = Array.Empty<TrackVideoProofDto>();
}

/// <summary>
/// Creator upsert for Behind The Track. Sending all-empty fields removes the
/// row entirely. YoutubeUrl must be a youtube.com / youtu.be URL.
/// </summary>
public class UpsertBehindTheTrackRequest
{
    [StringLength(5000)]
    [SafeMetadata]
    public string? Story { get; set; }

    [StringLength(200)]
    [SafeMetadata]
    public string? DAW { get; set; }

    [StringLength(2000)]
    [SafeMetadata]
    public string? VocalChain { get; set; }

    [StringLength(5000)]
    [SafeMetadata]
    public string? PromptNotes { get; set; }

    [StringLength(5000)]
    [SafeMetadata]
    public string? ProductionNotes { get; set; }

    [StringLength(5000)]
    [SafeMetadata]
    public string? HumanContributionNotes { get; set; }

    [StringLength(500)]
    public string? YoutubeUrl { get; set; }

    /// <summary>Tool names used (DAWs, AI models, instruments…), max 30 of 100 chars.</summary>
    public List<string>? ToolsUsed { get; set; }
}

/// <summary>Public-safe proof-video payload.</summary>
public class TrackVideoProofDto
{
    public string Id { get; set; } = "";
    public string TrackId { get; set; } = "";
    public string VideoType { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public string Visibility { get; set; } = "public";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Create a new proof video for a track.</summary>
public class CreateProofVideoRequest
{
    [Required]
    [RegularExpression("^(YouTube|External)$", ErrorMessage = "VideoType must be 'YouTube' or 'External'.")]
    public string VideoType { get; set; } = "";

    [Required]
    [StringLength(2000)]
    public string Url { get; set; } = "";

    [StringLength(200)]
    [SafeMetadata]
    public string? Title { get; set; }

    [StringLength(2000)]
    [SafeMetadata]
    public string? Description { get; set; }

    public int? SortOrder { get; set; }

    [RegularExpression("^(public|hidden)$", ErrorMessage = "Visibility must be 'public' or 'hidden'.")]
    public string? Visibility { get; set; }
}

/// <summary>Partial update for an existing proof video — only provided fields change.</summary>
public class UpdateProofVideoRequest
{
    [RegularExpression("^(YouTube|External)$", ErrorMessage = "VideoType must be 'YouTube' or 'External'.")]
    public string? VideoType { get; set; }

    [StringLength(2000)]
    public string? Url { get; set; }

    [StringLength(200)]
    [SafeMetadata]
    public string? Title { get; set; }

    [StringLength(2000)]
    [SafeMetadata]
    public string? Description { get; set; }

    public int? SortOrder { get; set; }

    [RegularExpression("^(public|hidden)$", ErrorMessage = "Visibility must be 'public' or 'hidden'.")]
    public string? Visibility { get; set; }
}
