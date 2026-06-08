using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Provenance;

/// <summary>
/// Upsert body for <c>POST /api/tracks/{id}/authorship</c>. All narrative fields
/// are optional; the request is an idempotent replace of the track's authorship
/// document. <see cref="CommercialRightsVerified"/> writes the track-level
/// attestation flag (batch-1 placeholder for the §9.5 verification flow).
/// </summary>
public sealed class TrackAuthorshipRequest
{
    [MaxLength(4000)]
    public string? Edits { get; set; }

    [MaxLength(4000)]
    public string? ArrangementNotes { get; set; }

    public bool LyricsAuthored { get; set; }

    [MaxLength(4000)]
    public string? ProcessNotes { get; set; }

    /// <summary>AI-use disclosure (free text). Non-empty satisfies the AI-disclosure compliance check.</summary>
    [MaxLength(4000)]
    public string? AiDisclosure { get; set; }

    /// <summary>Creator attestation that they hold commercial rights to the work.</summary>
    public bool CommercialRightsVerified { get; set; }
}
