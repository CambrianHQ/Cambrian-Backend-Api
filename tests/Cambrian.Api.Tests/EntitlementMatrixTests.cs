using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Unit tests for the plan-level entitlement matrix: TierManifest limits/feature flags
/// and PlanEntitlementService resolution. This is the source of truth behind
/// GET /api/me/entitlements.
/// </summary>
public sealed class EntitlementMatrixTests
{
    private static PlanEntitlementService CreateService(ApplicationUser? user, Subscription? sub)
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        users.FindByIdAsync(Arg.Any<string>()).Returns(user);

        var subs = Substitute.For<ISubscriptionRepository>();
        subs.GetActiveAsync(Arg.Any<string>()).Returns(sub);

        var logger = Substitute.For<ILogger<PlanEntitlementService>>();
        return new PlanEntitlementService(users, subs, logger);
    }

    // ── TierManifest limits ──

    [Theory]
    [InlineData(CreatorTier.Free, 10)]
    [InlineData(CreatorTier.Creator, null)]
    [InlineData(CreatorTier.Pro, null)]
    public void TierManifest_MaxTracks_MatchesSpec(CreatorTier tier, int? expectedMax)
    {
        Assert.Equal(expectedMax, TierManifest.For(tier).UploadLimit);
    }

    [Theory]
    [InlineData("free", 0)]
    [InlineData("creator", 1500)]
    [InlineData("pro", 3900)]
    public void TierManifest_Prices_MatchSpec(string slug, int expectedCents)
    {
        Assert.Equal(expectedCents, TierManifest.For(slug).PriceCents);
    }

    [Fact]
    public void TierManifest_FeatureFlags_GateByTier()
    {
        var free = TierManifest.Free.FeatureFlags;
        var creator = TierManifest.Creator.FeatureFlags;
        var pro = TierManifest.Pro.FeatureFlags;

        // All tiers
        Assert.True(free["provenanceStamp"]);
        Assert.True(free["complianceScoreRead"]);

        // Creator + Pro only
        Assert.False(free["unlimitedTracks"]);
        Assert.True(creator["unlimitedTracks"]);
        Assert.True(pro["unlimitedTracks"]);
        Assert.True(creator["fullProvenanceSuite"]);

        // Pro only
        Assert.False(creator["apiAccess"]);
        Assert.False(creator["syncPool"]);
        Assert.True(pro["apiAccess"]);
        Assert.True(pro["syncPool"]);
        Assert.True(pro["copyrightOfficeAssist"]);
    }

    // ── PlanEntitlementService resolution ──

    [Fact]
    public async Task Resolve_FreeUser_HasTenTrackLimitAndNoUnlimited()
    {
        var user = new ApplicationUser { Id = "u1", CreatorTier = CreatorTier.Free };
        var svc = CreateService(user, sub: null);

        var result = await svc.ResolveAsync("u1");

        Assert.Equal("free", result.Plan);
        Assert.Equal("active", result.Status);
        Assert.Equal(10, result.Limits.MaxTracks);
        Assert.False(result.Features["unlimitedTracks"]);
        Assert.True(result.Features["provenanceStamp"]);
    }

    [Fact]
    public async Task Resolve_CreatorUser_HasUnlimitedTracksAndSuite()
    {
        var user = new ApplicationUser { Id = "u2", CreatorTier = CreatorTier.Creator };
        var sub = new Subscription { UserId = "u2", Plan = "creator", Status = "active" };
        var svc = CreateService(user, sub);

        var result = await svc.ResolveAsync("u2");

        Assert.Equal("creator", result.Plan);
        Assert.Null(result.Limits.MaxTracks);
        Assert.True(result.Features["unlimitedTracks"]);
        Assert.True(result.Features["pdfCertificates"]);
        Assert.False(result.Features["apiAccess"]);
    }

    [Fact]
    public async Task Resolve_ProUser_HasAllProFeatures()
    {
        var user = new ApplicationUser { Id = "u3", CreatorTier = CreatorTier.Pro };
        var sub = new Subscription { UserId = "u3", Plan = "pro", Status = "active" };
        var svc = CreateService(user, sub);

        var result = await svc.ResolveAsync("u3");

        Assert.Equal("pro", result.Plan);
        Assert.Null(result.Limits.MaxTracks);
        Assert.True(result.Features["apiAccess"]);
        Assert.True(result.Features["syncPool"]);
        Assert.True(result.Features["bulkUpload"]);
    }

    [Fact]
    public async Task Resolve_PastDueSubscription_ReportsPastDueStatus()
    {
        var user = new ApplicationUser { Id = "u4", CreatorTier = CreatorTier.Creator, SubscriptionStatus = "PastDue" };
        var sub = new Subscription { UserId = "u4", Plan = "creator", Status = "past_due" };
        var svc = CreateService(user, sub);

        var result = await svc.ResolveAsync("u4");

        Assert.Equal("past_due", result.Status);
    }

    [Fact]
    public async Task Resolve_UnknownUser_Throws()
    {
        var svc = CreateService(user: null, sub: null);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.ResolveAsync("missing"));
    }
}
