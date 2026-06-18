using System.Text.Json.Serialization;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;

namespace Cambrian.Application.DTOs.Entitlements;

/// <summary>
/// Flat DTO surface for Entitlement rows. Controllers must never return the
/// domain entity directly (governance §10). Enums serialize as their string names
/// (e.g. "License", not 3) so the contract is stable and self-describing.
/// </summary>
public sealed class EntitlementDto
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EntitlementResourceType ResourceType { get; set; }
    public string ResourceId { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EntitlementAccessLevel AccessLevel { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EntitlementSourceType SourceType { get; set; }
    public string? SourceId { get; set; }
    public DateTime GrantedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    public static EntitlementDto From(Entitlement e) => new()
    {
        Id = e.Id,
        UserId = e.UserId,
        ResourceType = e.ResourceType,
        ResourceId = e.ResourceId,
        AccessLevel = e.AccessLevel,
        SourceType = e.SourceType,
        SourceId = e.SourceId,
        GrantedAt = e.GrantedAt,
        ExpiresAt = e.ExpiresAt,
        RevokedAt = e.RevokedAt,
        RevokedReason = e.RevokedReason,
    };
}
