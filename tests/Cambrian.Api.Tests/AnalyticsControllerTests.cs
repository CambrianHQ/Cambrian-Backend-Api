using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for AnalyticsController access control.
/// </summary>
public sealed class AnalyticsControllerTests
{
    private readonly IAnalyticsRepository _analytics = Substitute.For<IAnalyticsRepository>();
    private readonly IAnalyticsService _analyticsService = Substitute.For<IAnalyticsService>();

    private AnalyticsController MakeController(string userId, bool isAdmin)
    {
        var controller = new AnalyticsController(_analytics, _analyticsService);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
        };
        if (isAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var identity = new ClaimsIdentity(claims, "Test");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    /// <summary>
    /// SECURITY — non-admin users must receive an empty list from GET /analytics/events,
    /// never raw event data belonging to other users.
    /// </summary>
    [Fact]
    public async Task Analytics_NonAdmin_ReturnsEmptyList()
    {
        var controller = MakeController("user-1", isAdmin: false);

        var result = await controller.Events(null, null, null, 100);

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<List<object>>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Empty(envelope.Data!);

        // Repository must NOT have been called — no data leaked
        await _analytics.DidNotReceive().QueryAsync(Arg.Any<string?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>());
    }

    /// <summary>
    /// REGRESSION — admins must still receive real event data.
    /// </summary>
    [Fact]
    public async Task Analytics_Admin_CallsRepository()
    {
        var controller = MakeController("admin-1", isAdmin: true);
        _analytics.QueryAsync(null, null, null, 100).Returns((IReadOnlyList<Cambrian.Domain.Entities.AnalyticsEvent>)new List<Cambrian.Domain.Entities.AnalyticsEvent>());

        await controller.Events(null, null, null, 100);

        await _analytics.Received(1).QueryAsync(null, null, null, 100);
    }
}
