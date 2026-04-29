using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Entitlements;

/// <summary>
/// Body for <c>DELETE /api/entitlements/{id}</c>. The reason is required so
/// audit logs capture intent (DELETE bodies are non-standard but acceptable
/// here — alternative is a header, which is worse DX).
/// </summary>
public sealed class RevokeEntitlementRequest
{
    [Required]
    [StringLength(500, MinimumLength = 1)]
    public string Reason { get; set; } = "";
}
