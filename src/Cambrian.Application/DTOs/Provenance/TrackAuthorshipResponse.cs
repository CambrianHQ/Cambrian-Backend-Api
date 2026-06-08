namespace Cambrian.Application.DTOs.Provenance;

/// <summary>Authorship document returned by the authorship GET/POST endpoints.</summary>
public sealed class TrackAuthorshipResponse
{
    public string? Edits { get; set; }

    public string? ArrangementNotes { get; set; }

    public bool LyricsAuthored { get; set; }

    public string? ProcessNotes { get; set; }

    public string? AiDisclosure { get; set; }

    /// <summary>Track-level commercial-rights attestation (mirrors <c>Track.CommercialRightsVerified</c>).</summary>
    public bool CommercialRightsVerified { get; set; }

    public DateTime UpdatedAt { get; set; }
}
