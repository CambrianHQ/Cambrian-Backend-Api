namespace Cambrian.Application.DTOs.Provenance;

/// <summary>
/// Provenance read for <c>GET /api/tracks/{id}/provenance</c>. Returns the free, instant
/// signed stamp immediately; the nested <see cref="Anchor"/> stays <c>pending</c> until the
/// batch-2 job anchors the track's batch on-chain.
/// </summary>
public sealed class ProvenanceResponse
{
    /// <summary>SHA-256 hex of the audio bytes; null until hashed (pre-backfill legacy rows).</summary>
    public string? ContentHash { get; set; }

    /// <summary>Hash algorithm label (always <c>SHA-256</c> in batch 1).</summary>
    public string HashAlgorithm { get; set; } = "SHA-256";

    /// <summary>UTC time the stamp was signed (truncated to whole seconds). Null until signed.</summary>
    public DateTime? SignedAt { get; set; }

    /// <summary>Base64 server signature over (contentHash, signedAt). Null until signed.</summary>
    public string? Signature { get; set; }

    /// <summary>Signature algorithm label (e.g. <c>ECDSA-P256-SHA256</c>).</summary>
    public string? SignatureAlgorithm { get; set; }

    /// <summary>Fingerprint of the signing public key (fetch the key from <c>/api/provenance/public-key</c>).</summary>
    public string? PublicKeyId { get; set; }

    /// <summary>On-chain anchor state (batched Merkle); <c>pending</c> until anchored.</summary>
    public ProvenanceAnchorState Anchor { get; set; } = new();
}

/// <summary>On-chain anchor state for a track under the batched-Merkle model.</summary>
public sealed class ProvenanceAnchorState
{
    /// <summary>Anchor lifecycle: <c>pending</c> | <c>anchored</c> | <c>failed</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Chain id once anchored (e.g. <c>base</c>); null while pending.</summary>
    public string? Chain { get; set; }

    /// <summary>The batch Merkle root written on-chain; null while pending.</summary>
    public string? MerkleRoot { get; set; }

    /// <summary>Transaction reference for the batch's single root-anchoring tx; null while pending.</summary>
    public string? RootTxRef { get; set; }

    /// <summary>JSON Merkle proof for this track's leaf to the root; null while pending.</summary>
    public string? MerkleProof { get; set; }

    /// <summary>UTC time the batch anchored; null while pending.</summary>
    public DateTime? AnchoredAt { get; set; }
}
