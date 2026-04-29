using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;

namespace Cambrian.Application.Interfaces;

public interface IEntitlementRepository
{
    Task<Entitlement> AddAsync(Entitlement row, CancellationToken ct = default);

    Task<Entitlement?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task UpdateAsync(Entitlement row, CancellationToken ct = default);

    /// <summary>
    /// Returns every row (including revoked / expired) for a given
    /// (user, resource). The access-check service applies the revoked /
    /// expired filters so callers can distinguish "never had access" from
    /// "had access and it was revoked."
    /// </summary>
    Task<List<Entitlement>> GetForUserResourceAsync(
        string userId,
        EntitlementResourceType resourceType,
        string resourceId,
        CancellationToken ct = default);

    Task<List<Entitlement>> GetForUserAsync(
        string userId,
        EntitlementResourceType? resourceType = null,
        bool includeRevoked = false,
        CancellationToken ct = default);
}
