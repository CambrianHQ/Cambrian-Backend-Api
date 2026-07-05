using System.ComponentModel.DataAnnotations;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.CreatorProfile;

/// <summary>
/// "What's in my studio" — optional, creator-authored gear/workflow section.
/// Every field is free text or free-text tags by design: niche DAWs, plugins,
/// and hardware must never be blocked by a dropdown taxonomy. Stored on
/// CreatorProfile as a JSON string (SocialLinks precedent), hidden publicly
/// when empty.
/// </summary>
public class StudioSetupDto
{
    /// <summary>Primary DAW(s), free text (e.g. "Ableton Live 12, Reaper for stem cleanup").</summary>
    [StringLength(200)]
    [SafeMetadata]
    public string? Daw { get; set; }

    /// <summary>AI tools tags (e.g. "Suno v5.5", "Udio", "RVC").</summary>
    public List<string>? AiTools { get; set; }

    /// <summary>Instrument tags (e.g. "Fender Jazz Bass", "kalimba").</summary>
    public List<string>? Instruments { get; set; }

    /// <summary>Hardware tags (interfaces, synths, controllers, monitors).</summary>
    public List<string>? Hardware { get; set; }

    /// <summary>Plugin tags (e.g. "FabFilter Pro-Q 4", "Valhalla VintageVerb").</summary>
    public List<string>? Plugins { get; set; }

    /// <summary>Anything-else gear tags (mics, pedals, field recorders…).</summary>
    public List<string>? Gear { get; set; }

    /// <summary>Creative chain / workflow description, free text.</summary>
    [StringLength(2000)]
    [SafeMetadata]
    public string? WorkflowNotes { get; set; }
}
