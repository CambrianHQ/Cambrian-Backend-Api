using System.ComponentModel.DataAnnotations;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Public "Behind The Track" payload — the creator's account of how a track
/// was made (creation story, optional process video, tools used).
/// </summary>
public class BehindTheTrackDto
{
    public string TrackId { get; set; } = "";
    public string? Story { get; set; }
    public string? YoutubeUrl { get; set; }
    public IReadOnlyList<string> ToolsUsed { get; set; } = Array.Empty<string>();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
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

    [StringLength(500)]
    public string? YoutubeUrl { get; set; }

    /// <summary>Tool names used (DAWs, AI models, instruments…), max 30 of 100 chars.</summary>
    public List<string>? ToolsUsed { get; set; }
}
