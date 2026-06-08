namespace Cambrian.Application.Interfaces;

/// <summary>
/// Batched on-chain anchor. A set of track leaf hashes is collected into a Merkle tree and the
/// single root is written to a cheap EVM L2 in <b>one transaction per batch</b>; each leaf gets a
/// Merkle proof to that root. This keeps the only on-chain cost at ~one L2 tx regardless of how
/// many tracks are in the batch — never one tx per track, never L1.
///
/// <para>Implementations are swappable behind this interface:
/// <list type="bullet">
///   <item><c>NoOpProvenanceAnchor</c> — computes a real Merkle root + proofs but a dev tx ref;
///   ships now so the batch-2 job can be built/tested locally. Not invoked in this batch.</item>
///   <item><c>BaseMerkleProvenanceAnchor</c> — recommended production anchor on Base (L2). The
///   batch-2 job calls this on a schedule. Documented seam; not implemented here.</item>
/// </list></para>
/// </summary>
public interface IProvenanceAnchor
{
    /// <summary>
    /// Anchor a batch of leaf hashes: build the Merkle root, write one tx, and return the root,
    /// transaction reference, and a proof for every leaf.
    /// </summary>
    Task<BatchAnchorResult> AnchorBatchAsync(IReadOnlyList<string> leafHashes, CancellationToken ct = default);
}

/// <summary>Result of anchoring one batch's Merkle root on-chain.</summary>
/// <param name="Chain">Chain identifier (e.g. <c>base</c>, <c>base-sepolia</c>).</param>
/// <param name="MerkleRoot">The batch Merkle root (hex) written on-chain.</param>
/// <param name="RootTxRef">Transaction reference for the single root-anchoring tx.</param>
/// <param name="AnchoredAt">UTC time the tx confirmed.</param>
/// <param name="Proofs">One proof per input leaf, in input order.</param>
public sealed record BatchAnchorResult(
    string Chain,
    string MerkleRoot,
    string RootTxRef,
    DateTime AnchoredAt,
    IReadOnlyList<LeafProof> Proofs);

/// <summary>A single leaf's Merkle proof (sibling path) to the batch root.</summary>
/// <param name="LeafHash">The leaf content hash (hex).</param>
/// <param name="ProofJson">JSON-encoded sibling path proving the leaf is in the root.</param>
public sealed record LeafProof(string LeafHash, string ProofJson);
