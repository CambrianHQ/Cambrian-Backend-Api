using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Provenance;

/// <summary>
/// Upsert body for <c>POST /api/tracks/{id}/authorship</c>. Partial update: a field
/// left <c>null</c> (omitted from the JSON body) leaves the stored value unchanged;
/// send an explicit value (including <c>""</c> for a string, or <c>false</c> for a
/// bool) to overwrite it. This lets a frontend save one section of the authorship
/// document (e.g. just the AI disclosure) without resending — and silently wiping —
/// every other section, including <see cref="CommercialRightsVerified"/>, the
/// track-level attestation flag (batch-1 placeholder for the §9.5 verification flow).
/// </summary>
public sealed class TrackAuthorshipRequest
{
    [MaxLength(4000)]
    public string? Edits { get; set; }

    [MaxLength(4000)]
    public string? ArrangementNotes { get; set; }

    public bool? LyricsAuthored { get; set; }

    [MaxLength(4000)]
    public string? ProcessNotes { get; set; }

    /// <summary>AI-use disclosure (free text). Non-empty satisfies the AI-disclosure compliance check.</summary>
    [MaxLength(4000)]
    public string? AiDisclosure { get; set; }

    /// <summary>Creator attestation that they hold commercial rights to the work.</summary>
    public bool? CommercialRightsVerified { get; set; }
}
