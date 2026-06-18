using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Cambrian.Domain.Enums;

namespace Cambrian.Application.DTOs.Entitlements;

/// <summary>
/// Body for <c>POST /api/entitlements/grant</c>. Admin-only — feature services
/// that grant entitlements inline (tips, subscriptions) call IEntitlementService
/// directly rather than going through the HTTP endpoint. Enum fields accept their
/// string names (and remain backward-compatible with integer values).
/// </summary>
public sealed class GrantEntitlementRequest
{
    [Required]
    public string UserId { get; set; } = "";

    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EntitlementResourceType ResourceType { get; set; }

    [Required]
    public string ResourceId { get; set; } = "";

    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EntitlementAccessLevel AccessLevel { get; set; }

    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EntitlementSourceType SourceType { get; set; }

    public string? SourceId { get; set; }

    public DateTime? ExpiresAt { get; set; }
}
