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

    /// <summary>Digital Audio Workstation used (e.g. Ableton, FL Studio, Logic Pro).</summary>
    public string? DAW { get; set; }

    /// <summary>Vocal chain / signal path notes (plugins, processing order).</summary>
    public string? VocalChain { get; set; }

    /// <summary>Prompts or prompt-engineering notes used with AI tools.</summary>
    public string? PromptNotes { get; set; }

    /// <summary>Free-form production process notes distinct from the creation story.</summary>
    public string? ProductionNotes { get; set; }

    /// <summary>What the human creator contributed versus AI-assisted elements.</summary>
    public string? HumanContributionNotes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
