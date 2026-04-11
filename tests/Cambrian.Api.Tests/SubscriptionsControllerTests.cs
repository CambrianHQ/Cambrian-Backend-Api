using System.Security.Claims;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Controller-level tests for SubscriptionsController covering plans (public),
/// current subscription, update (with paid-plan guard), cancel, and history.
/// </summary>
public sealed class SubscriptionsControllerTests
{
    private readonly ISubscriptionService _subscriptions = Substitute.For<ISubscriptionService>();
    private readonly SubscriptionsController _controller;

    public SubscriptionsControllerTests()
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

    // ── Plans (AllowAnonymous) ──

    [Fact]
    public async Task Plans_ReturnsOk_WithPlanList()
    {
        _subscriptions.GetPlansAsync().Returns(new List<PlanResponse>
        {
            new() { Name = "free", Description = "Free tier", PriceCents = 0, Interval = "month" },
            new() { Name = "creator", Description = "Creator tier", PriceCents = 999, Interval = "month" }
        });

        var result = await _controller.Plans();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Current ──

    [Fact]
    public async Task Current_ReturnsOk_WithSubscription()
    {
        SetupUser();
        _subscriptions.GetCurrentAsync("user-1").Returns(new SubscriptionResponse
        {
            Id = Guid.NewGuid(),
            Plan = "free",
            Status = "active",
            StartedAt = DateTime.UtcNow
        });

        var result = await _controller.Current();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Update ──

    [Fact]
    public async Task Update_FreePlan_ReturnsOk()
    {
        SetupUser();
        var request = new UpdateSubscriptionRequest { Plan = "free" };
        _subscriptions.UpdateAsync(request, "user-1").Returns(new SubscriptionResponse
        {
            Plan = "free",
            Status = "active"
        });

        var result = await _controller.Update(request);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Update_PaidPlan_Returns402_WhenNotAlreadyOnPlan()
    {
        SetupUser();
        _subscriptions.GetCurrentAsync("user-1").Returns(new SubscriptionResponse
        {
            Plan = "free",
            Status = "active"
        });

        var request = new UpdateSubscriptionRequest { Plan = "paid" };
        var result = await _controller.Update(request);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, obj.StatusCode);
    }

    [Fact]
    public async Task Update_CreatorPlan_Returns402_WhenNotAlreadyOnPlan()
    {
        SetupUser();
        _subscriptions.GetCurrentAsync("user-1").Returns(new SubscriptionResponse
        {
            Plan = "free",
            Status = "active"
        });

        var request = new UpdateSubscriptionRequest { Plan = "creator" };
        var result = await _controller.Update(request);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(402, obj.StatusCode);
    }

    [Fact]
    public async Task Update_PaidPlan_AllowsResync_WhenAlreadyOnSamePlan()
    {
        SetupUser();
        _subscriptions.GetCurrentAsync("user-1").Returns(new SubscriptionResponse
        {
            Plan = "paid",
            Status = "active"
        });
        var request = new UpdateSubscriptionRequest { Plan = "paid" };
        _subscriptions.UpdateAsync(request, "user-1").Returns(new SubscriptionResponse
        {
            Plan = "paid",
            Status = "active"
        });

        var result = await _controller.Update(request);

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Cancel ──

    [Fact]
    public async Task Cancel_ReturnsOk_OnSuccess()
    {
        SetupUser();
        _subscriptions.CancelAsync("user-1").Returns(Task.CompletedTask);

        var result = await _controller.Cancel();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Cancel_ReturnsError_OnInvalidOperation()
    {
        SetupUser();
        _subscriptions.CancelAsync("user-1")
            .ThrowsAsync(new InvalidOperationException("No active subscription."));

        var result = await _controller.Cancel();

        var obj = Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── History ──

    [Fact]
    public async Task History_ReturnsOk_WithList()
    {
        SetupUser();
        _subscriptions.GetHistoryAsync("user-1").Returns(new List<SubscriptionResponse>());

        var result = await _controller.History();

        Assert.IsType<OkObjectResult>(result);
    }
}
