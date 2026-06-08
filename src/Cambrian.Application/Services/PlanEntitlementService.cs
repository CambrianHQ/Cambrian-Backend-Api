using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Entitlements;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <summary>
/// Resolves plan-level entitlements from the user's <see cref="ApplicationUser.CreatorTier"/>
/// (the authoritative entitlement, kept in sync by the Stripe webhook) and their active
/// subscription row (for status). Limits and feature flags come from <see cref="TierManifest"/>.
/// </summary>
public sealed class PlanEntitlementService : IPlanEntitlementService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly ILogger<PlanEntitlementService> _logger;

    public PlanEntitlementService(
        UserManager<ApplicationUser> users,
        ISubscriptionRepository subscriptions,
        ILogger<PlanEntitlementService> logger)
    {
        _users = users;
        _subscriptions = subscriptions;
        _logger = logger;
    }

    public async Task<MeEntitlementsResponse> ResolveAsync(string userId, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null)
            throw new KeyNotFoundException($"User {userId} not found.");

        // CreatorTier is the source of truth for limits/features (webhook keeps it in sync).
        var tier = TierManifest.For(user.CreatorTier);

        // Status reflects the active subscription; a Free user with no subscription is "active".
        var sub = await _subscriptions.GetActiveAsync(userId);
        var status = NormalizeStatus(sub?.Status, user.SubscriptionStatus, tier.Slug);

        _logger.LogInformation(
            "EVENT: EntitlementsResolved userId:{UserId} plan:{Plan} status:{Status}",
            userId, tier.Slug, status);

        return new MeEntitlementsResponse
        {
            Plan = tier.Slug,
            Status = status,
            Limits = new EntitlementLimits { MaxTracks = tier.UploadLimit },
            Features = tier.FeatureFlags
        };
    }

    public async Task RequireFeatureAsync(string userId, string featureKey, CancellationToken ct = default)
    {
        var entitlements = await ResolveAsync(userId, ct);

        if (!entitlements.Features.TryGetValue(featureKey, out var enabled) || !enabled)
        {
            _logger.LogInformation(
                "EVENT: FeatureGateBlocked userId:{UserId} feature:{Feature} plan:{Plan}",
                userId, featureKey, entitlements.Plan);
            throw new UpgradeRequiredException(
                "Your current plan does not include this feature. Upgrade your plan to unlock it.");
        }
    }

    private static string NormalizeStatus(string? subscriptionStatus, string? userSubscriptionStatus, string tierSlug)
    {
        // Free tier has no recurring billing — always "active".
        if (tierSlug == "free")
            return "active";

        // Prefer the subscription row; fall back to the denormalized user flag.
        var raw = !string.IsNullOrWhiteSpace(subscriptionStatus)
            ? subscriptionStatus
            : userSubscriptionStatus;

        return (raw ?? "active").Trim().ToLowerInvariant() switch
        {
            "active" => "active",
            "cancelled" or "canceled" => "cancelled",
            "expired" => "expired",
            "pastdue" or "past_due" => "past_due",
            _ => "active"
        };
    }
}
