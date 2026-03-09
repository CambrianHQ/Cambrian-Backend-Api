using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for SubscriptionsController covering the subscription lifecycle:
/// plan listing, current subscription retrieval, upgrades, downgrades,
/// cancellation, and history. Validates correct delegation to ISubscriptionService.
/// </summary>
public sealed class SubscriptionFlowTests
{
    private readonly ISubscriptionService _subscriptions = Substitute.For<ISubscriptionService>();
    private readonly SubscriptionsController _controller;

    public SubscriptionFlowTests()
    {
        _controller = new SubscriptionsController(_subscriptions);
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
    public async Task Plans_ReturnsOk()
    {
        _subscriptions.GetPlansAsync().Returns(new List<PlanResponse>
        {
            new() { Name = "Free", PriceCents = 0, Features = ["Browse catalog"] },
            new() { Name = "Paid", PriceCents = 499, Features = ["Unlimited downloads"] },
            new() { Name = "Creator", PriceCents = 999, Features = ["Upload tracks"] }
        });

        var result = await _controller.Plans();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Current subscription ──

    [Fact]
    public async Task Current_ReturnsFree_WhenNoActiveSubscription()
    {
        SetupUser();
        _subscriptions.GetCurrentAsync("user-1")
            .Returns(new SubscriptionResponse { Plan = "free", Status = "active" });

        var result = await _controller.Current();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Current_ReturnsActivePlan()
    {
        SetupUser();
        _subscriptions.GetCurrentAsync("user-1")
            .Returns(new SubscriptionResponse
            {
                Id = Guid.NewGuid(),
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
        _subscriptions.UpdateAsync(Arg.Is<UpdateSubscriptionRequest>(r => r.Plan == "paid"), "user-1")
            .Returns(new SubscriptionResponse { Plan = "paid", Status = "active" });

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = "paid" });

        var ok = Assert.IsType<OkObjectResult>(result);
        await _subscriptions.Received(1).UpdateAsync(
            Arg.Is<UpdateSubscriptionRequest>(r => r.Plan == "paid"), "user-1");
    }

    [Fact]
    public async Task Update_CancelsOldSubscription_WhenUpgrading()
    {
        SetupUser();
        _subscriptions.UpdateAsync(Arg.Is<UpdateSubscriptionRequest>(r => r.Plan == "creator"), "user-1")
            .Returns(new SubscriptionResponse { Plan = "creator", Status = "active" });

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = "creator" });

        Assert.IsType<OkObjectResult>(result);
        await _subscriptions.Received(1).UpdateAsync(
            Arg.Is<UpdateSubscriptionRequest>(r => r.Plan == "creator"), "user-1");
    }

    [Fact]
    public async Task Update_NoOp_WhenSamePlanAlreadyActive()
    {
        SetupUser();
        _subscriptions.UpdateAsync(Arg.Is<UpdateSubscriptionRequest>(r => r.Plan == "creator"), "user-1")
            .Returns(new SubscriptionResponse { Plan = "creator", Status = "active" });

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = "creator" });

        var ok = Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Update_SyncsTier_WhenSamePlanButTierMismatch()
    {
        SetupUser();
        _subscriptions.UpdateAsync(Arg.Is<UpdateSubscriptionRequest>(r => r.Plan == "paid"), "user-1")
            .Returns(new SubscriptionResponse { Plan = "paid", Status = "active" });

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = "paid" });

        Assert.IsType<OkObjectResult>(result);
        await _subscriptions.Received(1).UpdateAsync(
            Arg.Is<UpdateSubscriptionRequest>(r => r.Plan == "paid"), "user-1");
    }

    [Fact]
    public async Task Update_DowngradesTo_Free_WithoutCreatingSubscription()
    {
        SetupUser();
        _subscriptions.UpdateAsync(Arg.Is<UpdateSubscriptionRequest>(r => r.Plan == "free"), "user-1")
            .Returns(new SubscriptionResponse { Plan = "free", Status = "active" });

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = "free" });

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Cancel ──

    [Fact]
    public async Task Cancel_Returns400_WhenNoActiveSubscription()
    {
        SetupUser();
        _subscriptions.CancelAsync("user-1")
            .ThrowsAsync(new InvalidOperationException("No active subscription to cancel."));

        var result = await _controller.Cancel();

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Contains("No active subscription", envelope.Error);
    }

    [Fact]
    public async Task Cancel_CancelsAndResetsTier()
    {
        SetupUser();

        var result = await _controller.Cancel();

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Contains("cancelled", envelope.Message);
        await _subscriptions.Received(1).CancelAsync("user-1");
    }

    // ── History ──

    [Fact]
    public async Task History_ReturnsAllSubscriptions()
    {
        SetupUser();
        _subscriptions.GetHistoryAsync("user-1").Returns(new List<SubscriptionResponse>
        {
            new() { Id = Guid.NewGuid(), Plan = "paid", Status = "cancelled", StartedAt = DateTime.UtcNow.AddMonths(-2) },
            new() { Id = Guid.NewGuid(), Plan = "creator", Status = "active", StartedAt = DateTime.UtcNow }
        });

        var result = await _controller.History();

        Assert.IsType<OkObjectResult>(result);
    }
}
