using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <summary>
/// Unified entitlement service. Legacy <see cref="CanDownloadAsync"/> stays
/// wired to <see cref="IPurchaseRepository"/> so existing download endpoints
/// remain correct. New platform features grant and check access via the
/// Entitlement table through the other methods on this service.
/// </summary>
public sealed class EntitlementService : IEntitlementService
{
    private readonly IPurchaseRepository _purchases;
    private readonly IEntitlementRepository _entitlements;
    private readonly ILogger<EntitlementService> _logger;

    public EntitlementService(
        IPurchaseRepository purchases,
        IEntitlementRepository entitlements,
        ILogger<EntitlementService> logger)
    {
        _purchases = purchases;
        _entitlements = entitlements;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> CanDownloadAsync(string userId, Guid trackId)
        => _purchases.HasCompletedPurchaseAsync(userId, trackId);

    /// <inheritdoc />
    public async Task<Entitlement> GrantAsync(
        string userId,
        EntitlementResourceType resourceType,
        string resourceId,
        EntitlementAccessLevel accessLevel,
        EntitlementSourceType sourceType,
        string? sourceId = null,
        DateTime? expiresAt = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(resourceId))
            throw new ArgumentException("resourceId is required.", nameof(resourceId));
        if (expiresAt is { } exp && exp <= DateTime.UtcNow)
            throw new ArgumentException("expiresAt must be in the future.", nameof(expiresAt));

        var row = new Entitlement
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            AccessLevel = accessLevel,
            SourceType = sourceType,
            SourceId = sourceId,
            GrantedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
        };

        var added = await _entitlements.AddAsync(row, ct);
        Observability.CambrianMetrics.EntitlementChanged.Add(1);
        _logger.LogInformation(
            "EVENT: entitlement_changed action:granted id:{EntId} userId:{UserId} resourceType:{ResourceType} resourceId:{ResourceId} level:{Level} sourceType:{SourceType} sourceId:{SourceId}",
            added.Id, userId, resourceType, resourceId, accessLevel, sourceType, sourceId);
        return added;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(Guid entitlementId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required.", nameof(reason));

        var row = await _entitlements.GetByIdAsync(entitlementId, ct);
        if (row is null || row.RevokedAt is not null)
            return false;

        row.RevokedAt = DateTime.UtcNow;
        row.RevokedReason = reason;
        await _entitlements.UpdateAsync(row, ct);

        Observability.CambrianMetrics.EntitlementChanged.Add(1);
        _logger.LogInformation(
            "EVENT: entitlement_changed action:revoked id:{EntId} userId:{UserId} reason:{Reason}",
            entitlementId, row.UserId, reason);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> HasAccessAsync(
        string userId,
        EntitlementResourceType resourceType,
        string resourceId,
        EntitlementAccessLevel requiredLevel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(resourceId))
            return false;

        var rows = await _entitlements.GetForUserResourceAsync(userId, resourceType, resourceId, ct);
        var now = DateTime.UtcNow;

        // Integer-ranked enum — License (3) satisfies Download (2) satisfies Stream (1).
        return rows.Any(e => e.RevokedAt is null
                          && (e.ExpiresAt is null || e.ExpiresAt > now)
                          && e.AccessLevel >= requiredLevel);
    }

    /// <inheritdoc />
    public Task<List<Entitlement>> GetForUserAsync(
        string userId,
        EntitlementResourceType? resourceType = null,
        bool includeRevoked = false,
        CancellationToken ct = default)
        => _entitlements.GetForUserAsync(userId, resourceType, includeRevoked, ct);
}
