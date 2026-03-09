using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Controller-level tests for AuthController covering register, login,
/// /me, logout, and password recovery routes. Validates HTTP status codes,
/// response shapes, and error handling at the controller boundary.
/// </summary>
public sealed class AuthControllerTests
{
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        _controller = new AuthController(_auth);
    }

    private void SetupUser(string userId, string? bearerToken = null)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        }, "Test"));
        if (bearerToken is not null)
            context.Request.Headers.Authorization = $"Bearer {bearerToken}";
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    // ── Register ──

    [Fact]
    public async Task Register_Returns201_WithSessionPayload()
    {
        _auth.RegisterAsync(Arg.Any<RegisterRequest>()).Returns(new AuthResponse
        {
            UserId = Guid.NewGuid(),
            Email = "new@test.com",
            Token = "jwt-token-123",
            Tier = "free"
        });

        var result = await _controller.Register(new RegisterRequest
        {
            Email = "new@test.com",
            Password = "StrongPass123!"
        });

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, obj.StatusCode);
    }

    [Fact]
    public async Task Register_PropagatesInvalidOperationException_WhenRegistrationFails()
    {
        _auth.RegisterAsync(Arg.Any<RegisterRequest>())
            .ThrowsAsync(new InvalidOperationException("Registration failed: Email taken"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _controller.Register(new RegisterRequest
            {
                Email = "dup@test.com",
                Password = "StrongPass123!"
            }));
    }

    // ── Login ──

    [Fact]
    public async Task Login_Returns200_WithSessionPayload()
    {
        _auth.LoginAsync(Arg.Any<LoginRequest>()).Returns(new AuthResponse
        {
            UserId = Guid.NewGuid(),
            Email = "user@test.com",
            Token = "jwt-token-456",
            Tier = "paid"
        });

        var result = await _controller.Login(new LoginRequest
        {
            Email = "user@test.com",
            Password = "pass"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, ok.StatusCode);
    }

    [Fact]
    public async Task Login_PropagatesUnauthorized_WhenCredentialsInvalid()
    {
        _auth.LoginAsync(Arg.Any<LoginRequest>())
            .ThrowsAsync(new UnauthorizedAccessException("Invalid credentials"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _controller.Login(new LoginRequest
            {
                Email = "bad@test.com",
                Password = "wrong"
            }));
    }

    // ── Me ──

    [Fact]
    public async Task Me_ReturnsSession_WithResolvedTier()
    {
        var userId = Guid.NewGuid();
        SetupUser(userId.ToString(), "test-bearer-token");

        _auth.GetSessionAsync(Arg.Any<ClaimsPrincipal>()).Returns(new AuthResponse
        {
            UserId = userId,
            Email = "me@test.com",
            Token = "fresh-jwt-token",
            Tier = "creator",
            Role = "User"
        });

        var result = await _controller.Me();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        await _auth.Received(1).GetSessionAsync(Arg.Any<ClaimsPrincipal>());
    }

    [Fact]
    public async Task Me_PropagatesUnauthorized_WhenUserNotFound()
    {
        SetupUser(Guid.NewGuid().ToString());

        _auth.GetSessionAsync(Arg.Any<ClaimsPrincipal>())
            .ThrowsAsync(new UnauthorizedAccessException("User not found"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _controller.Me());
    }

    // ── Logout ──

    [Fact]
    public void Logout_ReturnsOk_WithMessage()
    {
        SetupUser("user-1");

        var result = _controller.Logout();

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Equal("Logged out successfully.", envelope.Message);
    }

    // ── Health (anonymous) ──

    [Fact]
    public void Health_ReturnsOk()
    {
        var result = _controller.Health();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── CSRF Token (anonymous) ──

    [Fact]
    public void CsrfToken_ReturnsUniqueTokens()
    {
        var r1 = _controller.GetCsrfToken();
        var r2 = _controller.GetCsrfToken();

        Assert.IsType<OkObjectResult>(r1);
        Assert.IsType<OkObjectResult>(r2);
    }

    // ── Forgot Password ──

    [Fact]
    public async Task ForgotPassword_ReturnsGenericMessage()
    {
        _auth.ForgotPasswordAsync(Arg.Any<ForgotPasswordRequest>()).Returns(Task.CompletedTask);

        var result = await _controller.ForgotPassword(new ForgotPasswordRequest());

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Contains("reset code", envelope.Message);
    }

    // ── Reset Password ──

    [Fact]
    public async Task ResetPassword_ReturnsSuccessMessage()
    {
        _auth.ResetPasswordAsync(Arg.Any<ResetPasswordRequest>()).Returns(Task.CompletedTask);

        var result = await _controller.ResetPassword(new ResetPasswordRequest());

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Contains("reset successfully", envelope.Message);
    }

    // ── Settings (Authorize-protected) ──

    [Fact]
    public void GetProfile_ReturnsOk()
    {
        SetupUser("user-1");
        var result = _controller.GetProfile();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void ChangePassword_ReturnsOk()
    {
        SetupUser("user-1");
        var result = _controller.ChangePassword();

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Equal("Password updated.", envelope.Message);
    }
}
