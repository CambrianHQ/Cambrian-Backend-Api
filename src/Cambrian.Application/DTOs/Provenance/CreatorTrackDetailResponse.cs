namespace Cambrian.Application.DTOs.Provenance;

/// <summary>
/// Consolidated creator-facing track detail for <c>GET /api/tracks/{id}/creator-detail</c> —
/// bundles provenance, compliance, authorship, and verification state so the
/// creator track-detail view loads in one round-trip.
/// </summary>
public sealed class CreatorTrackDetailResponse
{
    public Guid TrackId { get; set; }

    public string CambrianTrackId { get; set; } = "";

    public string Title { get; set; } = "";

    public ProvenanceResponse Provenance { get; set; } = new();

    public ComplianceScoreResponse Compliance { get; set; } = new();

    /// <summary>Null when the creator has not documented authorship yet.</summary>
    public TrackAuthorshipResponse? Authorship { get; set; }

    public TrackVerificationState Verification { get; set; } = new();
}

/// <summary>Track-level verification flags surfaced on the creator detail bundle.</summary>
public sealed class TrackVerificationState
{
    public bool CommercialRightsVerified { get; set; }

    /// <summary>True once provenance is anchored on-chain.</summary>
    public bool ProvenanceAnchored { get; set; }
}
