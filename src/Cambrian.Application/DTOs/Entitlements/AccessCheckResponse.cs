using Cambrian.Domain.Enums;

namespace Cambrian.Application.DTOs.Entitlements;

/// <summary>
/// Response for <c>GET /api/entitlements/access</c>. Echoes the query back so
/// clients can correlate when multiple checks race in flight.
/// </summary>
public sealed class AccessCheckResponse
{
    public bool HasAccess { get; set; }
    public EntitlementResourceType ResourceType { get; set; }
    public string ResourceId { get; set; } = "";
    public EntitlementAccessLevel RequiredLevel { get; set; }
}
