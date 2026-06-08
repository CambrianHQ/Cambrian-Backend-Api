using Cambrian.Application.DTOs.Provenance;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <inheritdoc cref="IProvenanceService" />
public sealed class ProvenanceService : IProvenanceService
{
    private readonly IProvenanceAnchorRepository _anchors;
    private readonly ILogger<ProvenanceService> _logger;

    public ProvenanceService(IProvenanceAnchorRepository anchors, ILogger<ProvenanceService> logger)
    {
        _anchors = anchors;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EnsureAnchorPendingAsync(Guid trackId, string contentHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
            return;

        // Idempotent: one anchor row per track.
        var existing = await _anchors.GetByTrackIdAsync(trackId, ct);
        if (existing is not null)
            return;

        await _anchors.AddAsync(new ProvenanceAnchor
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            ContentHash = contentHash,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }, ct);

        _logger.LogInformation("EVENT: ProvenanceAnchorPending trackId:{TrackId}", trackId);
    }

    /// <inheritdoc />
    public async Task<ProvenanceAnchorState> GetAnchorStateAsync(Guid trackId, CancellationToken ct = default)
    {
        var anchor = await _anchors.GetByTrackIdAsync(trackId, ct);

        return new ProvenanceAnchorState
        {
            Status = anchor?.Status ?? "pending",
            Chain = anchor?.Chain,
            MerkleRoot = anchor?.MerkleRoot,
            RootTxRef = anchor?.RootTxRef,
            MerkleProof = anchor?.MerkleProof,
            AnchoredAt = anchor?.AnchoredAt,
        };
    }
}
