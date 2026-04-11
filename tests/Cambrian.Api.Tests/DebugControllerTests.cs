using System.Security.Claims;
using Cambrian.Api.Controllers;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class DebugControllerTests
{
    [Fact]
    public async Task LatestPasswordReset_ReturnsNotFound_OutsideLocalDiagnostics()
    {
        var debug = Substitute.For<IDebugService>();
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var controller = BuildController(debug, env);

        var result = await controller.LatestPasswordReset("user@test.com", null);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task LatestPasswordReset_ReturnsOk_InDevelopment()
    {
        var debug = Substitute.For<IDebugService>();
        debug.GetLatestLocalPasswordResetAsync("user@test.com", null)
            .Returns(new { recipient = "user@test.com", code = "123456" });
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var controller = BuildController(debug, env);

        var result = await controller.LatestPasswordReset("user@test.com", null);

        Assert.IsType<OkObjectResult>(result);
    }

    private static DebugController BuildController(IDebugService debug, IWebHostEnvironment env)
    {
        var controller = new DebugController(debug, env, Substitute.For<ILogger<DebugController>>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "admin-1"),
                    new Claim(ClaimTypes.Role, "Admin")
                }, "Test"))
            }
        };
        return controller;
    }
}
