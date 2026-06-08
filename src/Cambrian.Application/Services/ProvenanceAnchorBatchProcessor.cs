using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <summary>
/// Drains pending provenance anchors into a single batched on-chain anchor: builds one Merkle tree
/// over the pending content hashes, writes the single root via <see cref="IProvenanceAnchor"/> (one
/// tx per batch — never per track), then records each track's Merkle proof and flips it to
/// <c>anchored</c>.
///
/// <para>Retry-safe by construction: <b>anchor first, then persist</b>. If the chain write fails the
/// rows stay <c>pending</c> and roll into the next batch. (If the tx succeeds but the DB write fails,
/// the rows also stay <c>pending</c> and get re-anchored in a fresh tx next run — a harmless duplicate
/// anchor, never data loss.) This is the unit the scheduled worker invokes each tick.</para>
/// </summary>
public sealed class ProvenanceAnchorBatchProcessor
{
    private readonly IProvenanceAnchorRepository _anchors;
    private readonly IProvenanceAnchor _anchor;
    private readonly ILogger<ProvenanceAnchorBatchProcessor> _logger;

    public ProvenanceAnchorBatchProcessor(
        IProvenanceAnchorRepository anchors,
        IProvenanceAnchor anchor,
        ILogger<ProvenanceAnchorBatchProcessor> logger)
    {
        _anchors = anchors;
        _anchor = anchor;
        _logger = logger;
    }

    /// <summary>Anchor one batch of up to <paramref name="maxBatchSize"/> pending tracks. Returns the count anchored.</summary>
    public async Task<int> ProcessBatchAsync(int maxBatchSize, CancellationToken ct = default)
    {
        var pending = await _anchors.GetPendingAsync(maxBatchSize, ct);
        if (pending.Count == 0)
            return 0;

        // Deterministic leaf order so the tree/root is reproducible and proofs line up by index.
        pending = pending.OrderBy(a => a.ContentHash, StringComparer.Ordinal).ToList();
        var leafHashes = pending.Select(a => a.ContentHash).ToList();

        BatchAnchorResult result;
        try
        {
            result = await _anchor.AnchorBatchAsync(leafHashes, ct);
        }
        catch (Exception ex)
        {
            // Leave rows pending — they roll into the next batch.
            _logger.LogError(ex, "EVENT: ProvenanceAnchorBatchFailed count:{Count}", pending.Count);
            return 0;
        }

        if (result.Proofs.Count != pending.Count)
        {
            _logger.LogError(
                "EVENT: ProvenanceAnchorBatchProofMismatch expected:{Expected} got:{Got} — leaving pending",
                pending.Count, result.Proofs.Count);
            return 0;
        }

        var batchId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        for (var i = 0; i < pending.Count; i++)
        {
            var row = pending[i];
            row.Status = "anchored";
            row.BatchId = batchId;
            row.Chain = result.Chain;
            row.MerkleRoot = result.MerkleRoot;
            row.RootTxRef = result.RootTxRef;
            row.MerkleProof = result.Proofs[i].ProofJson; // matched by index (duplicate audio → identical leaves)
            row.AnchoredAt = result.AnchoredAt;
            row.UpdatedAt = now;
        }

        await _anchors.UpdateRangeAsync(pending, ct);

        _logger.LogInformation(
            "EVENT: ProvenanceAnchorBatchAnchored count:{Count} batchId:{BatchId} chain:{Chain} root:{Root} tx:{Tx}",
            pending.Count, batchId, result.Chain, result.MerkleRoot, result.RootTxRef);

        return pending.Count;
    }
}
