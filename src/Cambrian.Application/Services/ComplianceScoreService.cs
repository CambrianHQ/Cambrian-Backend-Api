using Cambrian.Application.DTOs.Provenance;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

/// <summary>
/// Deterministic compliance score. Five equally-weighted checks (20 pts each;
/// pass = full, warn = half, fail = 0) sum to a 0–100 score. Every rule is defined
/// here so the surface is easy to extend or re-weight.
/// </summary>
public sealed class ComplianceScoreService : IComplianceScoreService
{
    private const int Pass = 20;
    private const int Warn = 10;
    private const int Fail = 0;

    private readonly IProvenanceAnchorRepository _anchors;
    private readonly ITrackAuthorshipRepository _authorship;

    public ComplianceScoreService(
        IProvenanceAnchorRepository anchors,
        ITrackAuthorshipRepository authorship)
    {
        _anchors = anchors;
        _authorship = authorship;
    }

    public async Task<ComplianceScoreResponse> ComputeAsync(Track track, CancellationToken ct = default)
    {
        var anchor = await _anchors.GetByTrackIdAsync(track.Id, ct);
        var authorship = await _authorship.GetByTrackIdAsync(track.Id, ct);

        var checks = new List<ComplianceCheck>
        {
            CommercialRights(track),
            AuthorshipDocumented(authorship),
            AiDisclosure(authorship),
            ProvenanceAnchored(anchor, stamped: !string.IsNullOrWhiteSpace(track.Signature)),
            MetadataComplete(track),
        };

        var score = checks.Sum(c => Points(c.Status));

        return new ComplianceScoreResponse { Score = score, Checks = checks };
    }

    private static int Points(string status) => status switch
    {
        "pass" => Pass,
        "warn" => Warn,
        _ => Fail,
    };

    private static ComplianceCheck CommercialRights(Track track) =>
        track.CommercialRightsVerified
            ? Check("commercialRightsVerified", "pass", "Commercial rights have been attested for this track.")
            : Check("commercialRightsVerified", "fail", "Commercial rights have not been verified yet.");

    private static ComplianceCheck AuthorshipDocumented(TrackAuthorship? a)
    {
        var hasNarrative = a is not null && (
            !string.IsNullOrWhiteSpace(a.Edits) ||
            !string.IsNullOrWhiteSpace(a.ArrangementNotes) ||
            !string.IsNullOrWhiteSpace(a.ProcessNotes) ||
            a.LyricsAuthored);

        if (hasNarrative)
            return Check("authorshipDocumented", "pass", "Authorship details have been documented.");

        return a is null
            ? Check("authorshipDocumented", "fail", "No authorship documentation has been provided.")
            : Check("authorshipDocumented", "warn", "Authorship record exists but has no details.");
    }

    private static ComplianceCheck AiDisclosure(TrackAuthorship? a) =>
        a is not null && !string.IsNullOrWhiteSpace(a.AiDisclosure)
            ? Check("aiDisclosurePresent", "pass", "An AI-use disclosure has been provided.")
            : Check("aiDisclosurePresent", "fail", "No AI-use disclosure has been provided.");

    // Progressive: anchored (pass) > signed stamp but anchor pending (warn) > nothing (fail).
    private static ComplianceCheck ProvenanceAnchored(ProvenanceAnchor? anchor, bool stamped)
    {
        if (anchor?.Status == "anchored")
            return Check("provenanceAnchored", "pass", "The content hash is anchored on-chain.");
        if (anchor?.Status == "failed")
            return Check("provenanceAnchored", "fail", "Provenance anchoring failed and needs a retry.");
        if (stamped)
            return Check("provenanceAnchored", "warn", "Provenance is signed; on-chain anchoring is pending.");
        return Check("provenanceAnchored", "fail", "This track has not been stamped or anchored.");
    }

    private static ComplianceCheck MetadataComplete(Track track)
    {
        // Concrete required set beyond the always-present title.
        var present = 0;
        var total = 5;
        if (!string.IsNullOrWhiteSpace(track.PrimaryGenre) ||
            !string.IsNullOrWhiteSpace(track.Genre) ||
            !string.IsNullOrWhiteSpace(track.Subgenre)) present++;
        if (!string.IsNullOrWhiteSpace(track.Description)) present++;
        if (!string.IsNullOrWhiteSpace(track.Mood)) present++;
        if (!string.IsNullOrWhiteSpace(track.Tempo)) present++;
        if (!string.IsNullOrWhiteSpace(track.CoverArtUrl)) present++;

        if (present == total)
            return Check("metadataComplete", "pass", "All recommended metadata fields are filled in.");
        if (present * 2 >= total)
            return Check("metadataComplete", "warn", $"Metadata is partially complete ({present}/{total} fields).");
        return Check("metadataComplete", "fail", $"Most metadata fields are missing ({present}/{total} fields).");
    }

    private static ComplianceCheck Check(string name, string status, string detail) =>
        new() { Name = name, Status = status, Detail = detail };
}
