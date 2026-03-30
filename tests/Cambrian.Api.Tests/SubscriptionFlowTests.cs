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
/// cancellation, and history.
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
        _subscriptions.GetPlansAsync().Returns(new List<PlanResponse>());

        var result = await _controller.Plans();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Current subscription ──

    [Fact]
    public async Task Current_ReturnsOk_WithCurrentSubscription()
    {
        SetupUser();
        _subscriptions.GetCurrentAsync("user-1").Returns(new SubscriptionResponse
        {
            Plan = "free",
            Status = "active"
        });

        var result = await _controller.Current();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Update subscription ──

    [Fact]
    public async Task Update_Returns402_ForPaidPlanUpgradeWithoutCheckout()
    {
        SetupUser();
        _subscriptions.GetCurrentAsync("user-1").Returns(new SubscriptionResponse
        {
            Plan = "free",
            Status = "active"
        });

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = "paid" });

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, obj.StatusCode);
    }

    [Fact]
    public async Task Update_AllowsResync_WhenAlreadyOnSamePaidPlan()
    {
        SetupUser();
        _subscriptions.GetCurrentAsync("user-1").Returns(new SubscriptionResponse
        {
            Plan = "paid",
            Status = "active"
        });
        _subscriptions.UpdateAsync(Arg.Any<UpdateSubscriptionRequest>(), "user-1")
            .Returns(new SubscriptionResponse { Plan = "paid", Status = "active" });

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = "paid" });

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Update_AllowsFreeDowngrade()
    {
        SetupUser();
        _subscriptions.UpdateAsync(Arg.Any<UpdateSubscriptionRequest>(), "user-1")
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
    public async Task Cancel_ReturnsOk_WhenCancelled()
    {
        SetupUser();
        _subscriptions.CancelAsync("user-1").Returns(Task.CompletedTask);

        var result = await _controller.Cancel();

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Contains("cancelled", envelope.Message);
    }

    // ── History ──

    [Fact]
    public async Task History_ReturnsAllSubscriptions()
    {
        SetupUser();
        _subscriptions.GetHistoryAsync("user-1").Returns(new List<SubscriptionResponse>
        {
            new() { Plan = "paid", Status = "cancelled", StartedAt = DateTime.UtcNow.AddMonths(-2) },
            new() { Plan = "creator", Status = "active", StartedAt = DateTime.UtcNow }
        });

        var result = await _controller.History();

        Assert.IsType<OkObjectResult>(result);
    }
}
