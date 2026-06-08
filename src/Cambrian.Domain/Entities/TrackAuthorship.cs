namespace Cambrian.Domain.Entities;

/// <summary>
/// Authorship documentation for a track — the creator's own record of how the
/// work was made (edits, arrangement, lyrics, process) plus an AI-use disclosure.
/// One row per track; the authorship endpoint upserts it.
///
/// <para>
/// This is part of the §9 "legitimacy" surface and feeds the compliance score
/// (authorship-documented + AI-disclosure-present checks). The track-level
/// commercial-rights flag lives on <see cref="Track.CommercialRightsVerified"/>,
/// not here, because it is a track verification state rather than narrative
/// documentation.
/// </para>
/// </summary>
public class TrackAuthorship
{
    public Guid Id { get; set; }

    /// <summary>FK to Tracks.Id. Unique — one authorship document per track.</summary>
    public Guid TrackId { get; set; }

    /// <summary>What edits/changes the creator made to the work.</summary>
    public string? Edits { get; set; }

    /// <summary>Arrangement notes (structure, instrumentation, performance).</summary>
    public string? ArrangementNotes { get; set; }

    /// <summary>Whether the creator authored the lyrics.</summary>
    public bool LyricsAuthored { get; set; }

    /// <summary>Free-form notes on the creative/production process.</summary>
    public string? ProcessNotes { get; set; }

    /// <summary>
    /// AI-use disclosure (free text). Non-empty satisfies the "AI disclosure
    /// present" compliance check. The structured DDEX AI-disclosure export is §9 item 6.
    /// </summary>
    public string? AiDisclosure { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ──
    public Track Track { get; set; } = null!;
}
