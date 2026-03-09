using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cambrian.Api.Tests;

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

    [Fact]
    public async Task Update_Returns400_WhenPlanMissing()
    {
        SetupUser();

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = " " });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Equal("Plan is required.", envelope.Error);
    }

    [Theory]
    [InlineData("paid")]
    [InlineData("creator")]
    [InlineData("PAID")]
    public async Task Update_Returns400_ForPaidPlanChanges(string plan)
    {
        SetupUser();

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = plan });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Contains("billing checkout", envelope.Error);
        await _subscriptions.DidNotReceive().UpdateAsync(Arg.Any<UpdateSubscriptionRequest>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Update_AllowsDowngradeToFree()
    {
        SetupUser();
        _subscriptions.UpdateAsync(Arg.Any<UpdateSubscriptionRequest>(), "user-1")
            .Returns(new SubscriptionResponse { Plan = "free", Status = "active" });

        var result = await _controller.Update(new UpdateSubscriptionRequest { Plan = "FREE" });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        await _subscriptions.Received(1).UpdateAsync(
            Arg.Is<UpdateSubscriptionRequest>(r => r.Plan == "free"),
            "user-1");
    }
}
