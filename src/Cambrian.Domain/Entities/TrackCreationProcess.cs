namespace Cambrian.Domain.Entities;

/// <summary>
/// "Behind The Track" — the creator's own account of how a track was made
/// (1:1 with Track). Makes AI involvement transparent instead of hidden:
/// the creation story, an optional process video, and the tools used are
/// shown publicly so AI musicians can prove their process.
/// </summary>
public sealed class TrackCreationProcess
{
    /// <summary>PK and FK to Tracks.Id (one process row per track).</summary>
    public Guid TrackId { get; set; }

    /// <summary>The creation story — original idea, production process.</summary>
    public string? Story { get; set; }

    /// <summary>Optional YouTube URL (process video / breakdown). Host-validated.</summary>
    public string? YoutubeUrl { get; set; }

    /// <summary>JSON array of tool names used (DAWs, AI models, instruments…).</summary>
    public string? ToolsUsed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
