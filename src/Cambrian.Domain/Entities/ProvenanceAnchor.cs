namespace Cambrian.Domain.Entities;

/// <summary>
/// Per-track on-chain anchor state under the <b>batched Merkle</b> model. Many tracks'
/// content hashes are collected into a Merkle tree and the single <see cref="MerkleRoot"/>
/// is written to a cheap L2 in one transaction (<see cref="RootTxRef"/>); each track keeps
/// its <see cref="MerkleProof"/> to that root. This row is created <c>pending</c> on upload
/// (a worklist entry) and filled in by the batch-anchor job (batch 2).
///
/// <para>Cost note: anchoring is never one tx per track — the chain write is amortized across
/// a whole batch, so the only on-chain cost is one L2 tx per batch regardless of track count.</para>
/// </summary>
public class ProvenanceAnchor
{
    public Guid Id { get; set; }

    /// <summary>FK to Tracks.Id. Unique — one anchor row per track.</summary>
    public Guid TrackId { get; set; }

    /// <summary>The track's leaf hash (SHA-256 hex) included in the Merkle tree.</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>Anchor lifecycle: <c>pending</c> | <c>anchored</c> | <c>failed</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Groups tracks anchored together in one batch. Assigned by the batch job.</summary>
    public Guid? BatchId { get; set; }

    /// <summary>Chain identifier once anchored (e.g. <c>base</c>, <c>base-sepolia</c>). Null while pending.</summary>
    public string? Chain { get; set; }

    /// <summary>The batch's Merkle root (hex) written on-chain. Null while pending.</summary>
    public string? MerkleRoot { get; set; }

    /// <summary>Transaction reference for the single root-anchoring tx. Null while pending.</summary>
    public string? RootTxRef { get; set; }

    /// <summary>JSON-encoded Merkle proof (sibling path) proving this leaf is in <see cref="MerkleRoot"/>. Null while pending.</summary>
    public string? MerkleProof { get; set; }

    /// <summary>UTC time the batch was anchored. Null while pending/failed.</summary>
    public DateTime? AnchoredAt { get; set; }

    /// <summary>Populated when <see cref="Status"/> is <c>failed</c>.</summary>
    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ──
    public Track Track { get; set; } = null!;
}
