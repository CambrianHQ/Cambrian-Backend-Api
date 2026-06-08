using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Provenance;

/// <summary>Body for <c>POST /api/provenance/verify</c> — verify a stamp against the platform key.</summary>
public sealed class ProvenanceVerifyRequest
{
    [Required]
    [MaxLength(64)]
    public string ContentHash { get; set; } = "";

    [Required]
    public DateTime SignedAt { get; set; }

    [Required]
    [MaxLength(200)]
    public string Signature { get; set; } = "";
}
