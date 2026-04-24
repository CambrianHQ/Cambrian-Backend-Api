using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Invariant tests for the anti-drift service layer.
/// These tests enforce the single source-of-truth contracts and must pass
/// whenever the relevant services are modified.
/// </summary>
public sealed class InvariantTests
{
    // ──────────────────────────────────────────────────────────────
    // ITrackVisibilityPolicy
    // ──────────────────────────────────────────────────────────────

    private readonly ITrackVisibilityPolicy _visibility = new TrackVisibilityPolicy();

    [Fact]
    public void Visibility_PublicTrack_AllowsAnonymous()
        => Assert.True(_visibility.CanAccess("public", "creator-1", null, false));

    [Fact]
    public void Visibility_PublicTrack_AllowsAuthenticated()
        => Assert.True(_visibility.CanAccess("public", "creator-1", "other-user", false));

    [Fact]
    public void Visibility_HiddenTrack_DeniesOtherUser()
        => Assert.False(_visibility.CanAccess("hidden", "creator-1", "other-user", false));

    [Fact]
    public void Visibility_HiddenTrack_DeniesAnonymous()
        => Assert.False(_visibility.CanAccess("hidden", "creator-1", null, false));

    [Fact]
    public void Visibility_HiddenTrack_AllowsOwner()
        => Assert.True(_visibility.CanAccess("hidden", "creator-1", "creator-1", false));

    [Fact]
    public void Visibility_HiddenTrack_AllowsAdmin()
        => Assert.True(_visibility.CanAccess("hidden", "creator-1", "admin-id", true));

    [Fact]
    public void Visibility_LimitedTrack_DeniesAnonymous()
        => Assert.False(_visibility.CanAccess("limited", "creator-1", null, false));

    [Fact]
    public void Visibility_LimitedTrack_DeniesOtherUser()
        => Assert.False(_visibility.CanAccess("limited", "creator-1", "other-user", false));

    [Fact]
    public void Visibility_LimitedTrack_AllowsOwner()
        => Assert.True(_visibility.CanAccess("limited", "creator-1", "creator-1", false));

    [Fact]
    public void Visibility_LimitedTrack_AllowsAdmin()
        => Assert.True(_visibility.CanAccess("limited", "creator-1", null, true));

    // ──────────────────────────────────────────────────────────────
    // IEntitlementService
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Entitlement_ReturnsFalse_WhenNoPurchaseExists()
    {
        var trackId = Guid.NewGuid();
        var repo = Substitute.For<IPurchaseRepository>();
        repo.HasCompletedPurchaseAsync("user-1", trackId).Returns(false);

        var svc = new EntitlementService(
            repo,
            Substitute.For<IEntitlementRepository>(),
            NullLogger<EntitlementService>.Instance);
        Assert.False(await svc.CanDownloadAsync("user-1", trackId));
    }

    [Fact]
    public async Task Entitlement_ReturnsTrue_WhenCompletedPurchaseExists()
    {
        var trackId = Guid.NewGuid();
        var repo = Substitute.For<IPurchaseRepository>();
        repo.HasCompletedPurchaseAsync("user-1", trackId).Returns(true);

        var svc = new EntitlementService(
            repo,
            Substitute.For<IEntitlementRepository>(),
            NullLogger<EntitlementService>.Instance);
        Assert.True(await svc.CanDownloadAsync("user-1", trackId));
    }

    [Fact]
    public async Task Entitlement_DelegatesToRepository_WithCorrectArguments()
    {
        var trackId = Guid.NewGuid();
        const string userId = "user-check";
        var repo = Substitute.For<IPurchaseRepository>();
        repo.HasCompletedPurchaseAsync(userId, trackId).Returns(true);

        var svc = new EntitlementService(
            repo,
            Substitute.For<IEntitlementRepository>(),
            NullLogger<EntitlementService>.Instance);
        await svc.CanDownloadAsync(userId, trackId);

        await repo.Received(1).HasCompletedPurchaseAsync(userId, trackId);
    }
}
