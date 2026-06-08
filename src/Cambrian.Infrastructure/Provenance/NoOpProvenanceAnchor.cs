using Cambrian.Application.Interfaces;
using Cambrian.Application.Provenance;
using Microsoft.Extensions.Logging;

namespace Cambrian.Infrastructure.Provenance;

/// <summary>
/// Dev/no-op batched anchor. Computes a <b>real</b> Merkle root + per-leaf proofs (so the batch-2
/// job and proof verification can be developed/tested locally) but does <b>not</b> touch any chain —
/// it returns a deterministic dev transaction reference. Nothing invokes this in batch 1, so tracks
/// remain <c>pending</c>. Swap for <c>BaseMerkleProvenanceAnchor</c> (Base L2) in batch 2 by changing
/// the DI registration.
/// </summary>
public sealed class NoOpProvenanceAnchor : IProvenanceAnchor
{
    private readonly ILogger<NoOpProvenanceAnchor> _logger;

    public NoOpProvenanceAnchor(ILogger<NoOpProvenanceAnchor> logger) => _logger = logger;

    public Task<BatchAnchorResult> AnchorBatchAsync(IReadOnlyList<string> leafHashes, CancellationToken ct = default)
    {
        if (leafHashes is null || leafHashes.Count == 0)
            throw new ArgumentException("At least one leaf hash is required.", nameof(leafHashes));

        var root = MerkleTree.ComputeRoot(leafHashes);
        var proofs = leafHashes
            .Select((leaf, i) => new LeafProof(leaf, MerkleTree.BuildProofJson(leafHashes, i)))
            .ToList();

        _logger.LogInformation(
            "EVENT: NoOpProvenanceAnchorBatch leaves:{Count} root:{Root} (no chain write)",
            leafHashes.Count, root);

        return Task.FromResult(new BatchAnchorResult(
            Chain: "dev",
            MerkleRoot: root,
            RootTxRef: $"dev:{root[..Math.Min(16, root.Length)]}",
            AnchoredAt: DateTime.UtcNow,
            Proofs: proofs));
    }
}
