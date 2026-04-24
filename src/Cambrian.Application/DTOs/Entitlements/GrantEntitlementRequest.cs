using System.ComponentModel.DataAnnotations;
using Cambrian.Domain.Enums;

namespace Cambrian.Application.DTOs.Entitlements;

/// <summary>
/// Body for <c>POST /api/entitlements/grant</c>. Admin-only — feature services
/// that grant entitlements inline (tips, subscriptions) call IEntitlementService
/// directly rather than going through the HTTP endpoint.
/// </summary>
public sealed class GrantEntitlementRequest
{
    [Required]
    public string UserId { get; set; } = "";

    [Required]
    public EntitlementResourceType ResourceType { get; set; }

    [Required]
    public string ResourceId { get; set; } = "";

    [Required]
    public EntitlementAccessLevel AccessLevel { get; set; }

    [Required]
    public EntitlementSourceType SourceType { get; set; }

    public string? SourceId { get; set; }

    public DateTime? ExpiresAt { get; set; }
}
