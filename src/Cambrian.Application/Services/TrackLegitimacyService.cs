using Cambrian.Application.DTOs.Provenance;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

/// <inheritdoc cref="ITrackLegitimacyService" />
public sealed class TrackLegitimacyService : ITrackLegitimacyService
{
    private readonly ITrackRepository _tracks;
    private readonly IProvenanceService _provenance;
    private readonly IProvenanceSigner _signer;
    private readonly IAuthorshipService _authorship;
    private readonly IComplianceScoreService _compliance;

    public TrackLegitimacyService(
        ITrackRepository tracks,
        IProvenanceService provenance,
        IProvenanceSigner signer,
        IAuthorshipService authorship,
        IComplianceScoreService compliance)
    {
        _tracks = tracks;
        _provenance = provenance;
        _signer = signer;
        _authorship = authorship;
        _compliance = compliance;
    }

    public async Task<ProvenanceResponse> GetProvenanceAsync(string trackId, CancellationToken ct = default)
    {
        var track = await ResolveAsync(trackId);
        var anchor = await _provenance.GetAnchorStateAsync(track.Id, ct);
        return BuildProvenance(track, anchor);
    }

    public async Task<ComplianceScoreResponse> GetComplianceScoreAsync(string trackId, CancellationToken ct = default)
    {
        var track = await ResolveAsync(trackId);
        return await _compliance.ComputeAsync(track, ct);
    }

    public async Task<TrackAuthorshipResponse> GetAuthorshipAsync(string trackId, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var track = await ResolveOwnedAsync(trackId, userId, isAdmin);
        var result = await _authorship.GetAsync(track, ct);
        return result ?? throw new KeyNotFoundException("Authorship has not been documented for this track.");
    }

    public async Task<TrackAuthorshipResponse> UpsertAuthorshipAsync(string trackId, string userId, bool isAdmin, TrackAuthorshipRequest request, CancellationToken ct = default)
    {
        var track = await ResolveOwnedAsync(trackId, userId, isAdmin);
        return await _authorship.UpsertAsync(track, request, ct);
    }

    public async Task<CreatorTrackDetailResponse> GetCreatorDetailAsync(string trackId, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var track = await ResolveOwnedAsync(trackId, userId, isAdmin);

        var anchor = await _provenance.GetAnchorStateAsync(track.Id, ct);
        var provenance = BuildProvenance(track, anchor);
        var compliance = await _compliance.ComputeAsync(track, ct);
        var authorship = await _authorship.GetAsync(track, ct);

        return new CreatorTrackDetailResponse
        {
            TrackId = track.Id,
            CambrianTrackId = track.CambrianTrackId,
            Title = track.Title,
            Provenance = provenance,
            Compliance = compliance,
            Authorship = authorship,
            Verification = new TrackVerificationState
            {
                CommercialRightsVerified = track.CommercialRightsVerified,
                ProvenanceAnchored = string.Equals(anchor.Status, "anchored", StringComparison.Ordinal),
            },
        };
    }

    // ── helpers ──

    private ProvenanceResponse BuildProvenance(Track track, ProvenanceAnchorState anchor) => new()
    {
        ContentHash = track.ContentHash,
        HashAlgorithm = "SHA-256",
        SignedAt = track.SignedAt,
        Signature = track.Signature,
        SignatureAlgorithm = track.Signature is null ? null : _signer.Algorithm,
        PublicKeyId = track.Signature is null ? null : _signer.KeyId,
        Anchor = anchor,
    };

    private async Task<Track> ResolveAsync(string trackId)
    {
        Track? track = null;
        if (Guid.TryParse(trackId, out var guid))
            track = await _tracks.GetByIdAsync(guid);
        else if (!string.IsNullOrWhiteSpace(trackId) && trackId.StartsWith("CAMB-TRK", StringComparison.OrdinalIgnoreCase))
            track = await _tracks.GetByCambrianTrackIdAsync(trackId);

        return track ?? throw new KeyNotFoundException("Track not found.");
    }

    private async Task<Track> ResolveOwnedAsync(string trackId, string userId, bool isAdmin)
    {
        var track = await ResolveAsync(trackId);
        if (!isAdmin && !string.Equals(track.CreatorId, userId, StringComparison.Ordinal))
            throw new ForbiddenException("You can only manage legitimacy data for your own tracks.");
        return track;
    }
}
