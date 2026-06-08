using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Provenance;

/// <summary>
/// Body for <c>POST /api/provenance/verify-inclusion</c> — verify that a track's content hash is
/// included in a batch Merkle root via its proof. Pair with the on-chain root (read from the anchor's
/// <c>rootTxRef</c>) for fully independent verification.
/// </summary>
public sealed class ProvenanceInclusionRequest
{
    [Required]
    [MaxLength(64)]
    public string ContentHash { get; set; } = "";

    /// <summary>JSON Merkle proof (the <c>merkleProof</c> from the provenance read).</summary>
    [Required]
    [MaxLength(8000)]
    public string MerkleProof { get; set; } = "";

    [Required]
    [MaxLength(128)]
    public string MerkleRoot { get; set; } = "";
}
