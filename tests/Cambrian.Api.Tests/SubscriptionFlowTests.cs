using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for SubscriptionsController covering the subscription lifecycle:
/// plan listing, current subscription retrieval, upgrades, downgrades,
/// cancellation, and tier synchronization with the ApplicationUser entity.
/// </summary>
public sealed class SubscriptionFlowTests
{
    private readonly ISubscriptionRepository _subscriptions = Substitute.For<ISubscriptionRepository>();
    private readonly UserManager<ApplicationUser> _users;
    private readonly SubscriptionsController _controller;

    public SubscriptionFlowTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _users = Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);

        _controller = new SubscriptionsController(_subscriptions, _users);
    }

    private void SetupUser(string userId = "user-1")
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        }, "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    // ── Plans (anonymous) ──

    [Fact]
    public void Plans_ReturnsOk()
    {
        var result = _controller.Plans();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Current subscription ──

    [Fact]
    public async Task Current_ReturnsFree_WhenNoActiveSubscription()
    {
        SetupUser();
        _subscriptions.GetActiveAsync("user-1").Returns((Subscription?)null);

        var result = await _controller.Current();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Current_ReturnsActivePlan()
    {
        SetupUser();
        _subscriptions.GetActiveAsync("user-1").Returns(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Plan = "creator",
            Status = "active",
            ExpiresAt = DateTime.UtcNow.AddMonths(1)
        });

        var result = await _controller.Current();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Update subscription ──

    [Fact]
    public async Task Update_CreatesNewSubscription_WhenNoneExists()
    {
        SetupUser();
        _subscriptions.GetActiveAsync("user-1").Returns((Subscription?)null);
        _users.FindByIdAsync("user-1").Returns(new ApplicationUser
        {
            Id = "user-1",
            Tier = "free"
        });
        _users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = "paid" });

        var ok = Assert.IsType<OkObjectResult>(result);
        await _subscriptions.Received(1).CreateAsync(Arg.Is<Subscription>(s =>
            s.Plan == "paid" && s.Status == "active" && s.UserId == "user-1"));
    }

    [Fact]
    public async Task Update_CancelsOldSubscription_WhenUpgrading()
    {
        SetupUser();
        var existingId = Guid.NewGuid();
        _subscriptions.GetActiveAsync("user-1").Returns(new Subscription
        {
            Id = existingId,
            UserId = "user-1",
            Plan = "paid",
            Status = "active"
        });
        _users.FindByIdAsync("user-1").Returns(new ApplicationUser
        {
            Id = "user-1",
            Tier = "paid"
        });
        _users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);

        await _controller.Update(new UpdateSubscriptionRequest { Plan = "creator" });

        await _subscriptions.Received(1).CancelAsync(existingId);
        await _subscriptions.Received(1).CreateAsync(Arg.Is<Subscription>(s =>
            s.Plan == "creator"));
    }

    [Fact]
    public async Task Update_NoOp_WhenSamePlanAlreadyActive()
    {
        SetupUser();
        _subscriptions.GetActiveAsync("user-1").Returns(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Plan = "creator",
            Status = "active"
        });
        _users.FindByIdAsync("user-1").Returns(new ApplicationUser
        {
            Id = "user-1",
            Tier = "creator"
        });

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = "creator" });

        var ok = Assert.IsType<OkObjectResult>(result);
        await _subscriptions.DidNotReceive().CancelAsync(Arg.Any<Guid>());
        await _subscriptions.DidNotReceive().CreateAsync(Arg.Any<Subscription>());
    }

    [Fact]
    public async Task Update_SyncsTier_WhenSamePlanButTierMismatch()
    {
        SetupUser();
        _subscriptions.GetActiveAsync("user-1").Returns(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Plan = "paid",
            Status = "active"
        });
        var user = new ApplicationUser { Id = "user-1", Tier = "free" };
        _users.FindByIdAsync("user-1").Returns(user);
        _users.UpdateAsync(user).Returns(IdentityResult.Success);

        await _controller.Update(new UpdateSubscriptionRequest { Plan = "paid" });

        Assert.Equal("paid", user.Tier);
        await _users.Received(1).UpdateAsync(user);
    }

    [Fact]
    public async Task Update_DowngradesTo_Free_WithoutCreatingSubscription()
    {
        SetupUser();
        var existingId = Guid.NewGuid();
        _subscriptions.GetActiveAsync("user-1").Returns(new Subscription
        {
            Id = existingId,
            UserId = "user-1",
            Plan = "paid",
            Status = "active"
        });
        var user = new ApplicationUser { Id = "user-1", Tier = "paid" };
        _users.FindByIdAsync("user-1").Returns(user);
        _users.UpdateAsync(user).Returns(IdentityResult.Success);

        await _controller.Update(new UpdateSubscriptionRequest { Plan = "free" });

        await _subscriptions.Received(1).CancelAsync(existingId);
        await _subscriptions.DidNotReceive().CreateAsync(Arg.Any<Subscription>());
        Assert.Equal("free", user.Tier);
    }

    // ── Cancel ──

    [Fact]
    public async Task Cancel_Returns400_WhenNoActiveSubscription()
    {
        SetupUser();
        _subscriptions.GetActiveAsync("user-1").Returns((Subscription?)null);

        var result = await _controller.Cancel();

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Contains("No active subscription", envelope.Error);
    }

    [Fact]
    public async Task Cancel_CancelsAndResetsTier()
    {
        SetupUser();
        var subId = Guid.NewGuid();
        _subscriptions.GetActiveAsync("user-1").Returns(new Subscription
        {
            Id = subId,
            UserId = "user-1",
            Plan = "creator",
            Status = "active"
        });
        var user = new ApplicationUser { Id = "user-1", Tier = "creator" };
        _users.FindByIdAsync("user-1").Returns(user);
        _users.UpdateAsync(user).Returns(IdentityResult.Success);

        var result = await _controller.Cancel();

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Contains("cancelled", envelope.Message);
        await _subscriptions.Received(1).CancelAsync(subId);
        Assert.Equal("free", user.Tier);
    }

    // ── History ──

    [Fact]
    public async Task History_ReturnsAllSubscriptions()
    {
        SetupUser();
        _subscriptions.GetHistoryAsync("user-1").Returns(new List<Subscription>
        {
            new() { Id = Guid.NewGuid(), Plan = "paid", Status = "cancelled", UserId = "user-1", StartedAt = DateTime.UtcNow.AddMonths(-2) },
            new() { Id = Guid.NewGuid(), Plan = "creator", Status = "active", UserId = "user-1", StartedAt = DateTime.UtcNow }
        });

        var result = await _controller.History();

        Assert.IsType<OkObjectResult>(result);
    }
}
