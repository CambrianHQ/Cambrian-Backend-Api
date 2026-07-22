using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.Auth;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Controller-level tests for AuthController covering register, login,
/// /me, logout, and password recovery routes. Validates HTTP status codes,
/// response shapes, and error handling at the controller boundary.
/// </summary>
[Trait("Category", "Critical")]
public sealed class AuthControllerTests
{
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISubscriptionRepository _subscriptions = Substitute.For<ISubscriptionRepository>();
    private readonly ICreatorIdentityRepository _creators = Substitute.For<ICreatorIdentityRepository>();
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITransactionManager _tx = Substitute.For<ITransactionManager>();
    private readonly IUsernameOnboardingService _usernameOnboarding = Substitute.For<IUsernameOnboardingService>();
    private readonly ILogger<AuthController> _logger = Substitute.For<ILogger<AuthController>>();
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);

        _tx.BeginTransactionAsync().Returns(Substitute.For<IAsyncDisposable>());

        var profiles = Substitute.For<ICreatorProfileRepository>();
        var capabilities = Substitute.For<ICapabilityResolver>();
        _controller = new AuthController(_auth, _subscriptions, _creators, profiles, capabilities, _userManager, _tx, _usernameOnboarding, _logger);

        // Provide a default HttpContext so AppendAuthCookie can resolve IHostEnvironment
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Testing");
        var services = new ServiceCollection();
        services.AddSingleton(env);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    private void SetupUser(string userId, string? bearerToken = null)
    {
        var context = _controller.ControllerContext.HttpContext as DefaultHttpContext ?? new DefaultHttpContext();
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
    public async Task Me_ReturnsUserProfile_WithSubscriptionTier()
    {
        var userId = Guid.NewGuid().ToString();
        SetupUser(userId, "test-bearer-token");

        _auth.GetCurrentUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(new UserProfileResponse
        {
            UserId = userId,
            Email = "me@test.com",
            Tier = "free",
            Role = "User"
        });

        _subscriptions.GetActiveAsync(userId).Returns(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Plan = "creator",
            Status = "active"
        });

        var result = await _controller.Me();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task Me_FallsBackToProfileTier_WhenNoActiveSubscription()
    {
        var userId = Guid.NewGuid().ToString();
        SetupUser(userId, "test-bearer-token");

        _auth.GetCurrentUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(new UserProfileResponse
        {
            UserId = userId,
            Email = "me@test.com",
            Tier = "paid",
            Role = "User"
        });

        _subscriptions.GetActiveAsync(userId).Returns((Subscription?)null);

        var result = await _controller.Me();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Me_Returns404_WhenProfileIsNull()
    {
        var userId = Guid.NewGuid().ToString();
        SetupUser(userId);

        _auth.GetCurrentUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(Task.FromResult<UserProfileResponse>(null!));

        var result = await _controller.Me();

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(notFound.Value);
        Assert.False(envelope.Success);
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
    public void CsrfToken_ReturnsOk()
    {
        var antiforgery = Substitute.For<IAntiforgery>();
        antiforgery.GetAndStoreTokens(Arg.Any<HttpContext>())
            .Returns(new AntiforgeryTokenSet("request-token", "cookie-token", "__RequestVerificationToken", "X-CSRF-TOKEN"));

        var result = _controller.CsrfToken(antiforgery);

        Assert.IsType<OkObjectResult>(result);
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
    public async Task ChangePassword_ReturnsOk()
    {
        SetupUser("user-1");
        _auth.ChangePasswordAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ChangePasswordRequest>())
            .Returns(Task.CompletedTask);

        var result = await _controller.ChangePassword(new ChangePasswordRequest
        {
            CurrentPassword = "old",
            NewPassword = "NewPass123!"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Equal("Password updated.", envelope.Message);
    }

    // ── Email change (two-step) ──

    [Fact]
    public async Task ChangeEmail_ReturnsVerificationMessage()
    {
        SetupUser("user-1");
        _auth.ChangeEmailAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ChangeEmailRequest>())
            .Returns(Task.CompletedTask);

        var result = await _controller.ChangeEmail(new ChangeEmailRequest
        {
            Password = "pass",
            NewEmail = "new@test.com"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Contains("Verification email sent", envelope.Message);
    }

    [Fact]
    public async Task VerifyEmailChange_ReturnsSuccess()
    {
        _auth.VerifyEmailChangeAsync(Arg.Any<string>())
            .Returns(Task.CompletedTask);

        var result = await _controller.VerifyEmailChange("u1.sometoken123");

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Equal("Email address updated successfully.", envelope.Message);
    }

    [Fact]
    public async Task VerifyEmailChange_Returns400_WhenTokenEmpty()
    {
        var result = await _controller.VerifyEmailChange("");

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Contains("token is required", envelope.Error);
    }

    // ── SetUsername delegation (F19 atomicity coverage moved to UsernameOnboardingServiceTests,
    //    since the validation/uniqueness/transaction/provisioning logic now lives in
    //    IUsernameOnboardingService, shared with POST /admin/users/{id}/set-username) ──

    [Fact]
    public async Task SetUsername_DelegatesToOnboardingService_AndReturnsFreshToken_OnSuccess()
    {
        var userId = "user-ok";
        SetupUser(userId);

        _usernameOnboarding.CompleteAsync(userId, "goodname", Arg.Any<CancellationToken>())
            .Returns(Application.Interfaces.UsernameOnboardingResult.Ok("goodname", "goodname", "User"));
        _auth.GenerateFreshTokenAsync(userId).Returns("fresh-jwt");

        var result = await _controller.SetUsername(new Application.DTOs.Auth.SetUsernameRequest { Username = "goodname" });

        Assert.IsType<OkObjectResult>(result);
        await _usernameOnboarding.Received(1).CompleteAsync(userId, "goodname", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetUsername_Returns409_WhenUsernameTaken()
    {
        var userId = "user-taken";
        SetupUser(userId);
        _usernameOnboarding.CompleteAsync(userId, "taken", Arg.Any<CancellationToken>())
            .Returns(Application.Interfaces.UsernameOnboardingResult.Failure("username_taken", "That username is already taken."));

        var result = await _controller.SetUsername(new Application.DTOs.Auth.SetUsernameRequest { Username = "taken" });

        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task SetUsername_Returns404_WhenUserNotFound()
    {
        var userId = "user-missing";
        SetupUser(userId);
        _usernameOnboarding.CompleteAsync(userId, "anything", Arg.Any<CancellationToken>())
            .Returns(Application.Interfaces.UsernameOnboardingResult.Failure("user_not_found", "User not found."));

        var result = await _controller.SetUsername(new Application.DTOs.Auth.SetUsernameRequest { Username = "anything" });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task SetUsername_Returns400_OnInvalidUsername()
    {
        var userId = "user-invalid";
        SetupUser(userId);
        _usernameOnboarding.CompleteAsync(userId, "!!", Arg.Any<CancellationToken>())
            .Returns(Application.Interfaces.UsernameOnboardingResult.Failure("invalid_username", "Username may only contain letters, numbers, hyphens, and underscores."));

        var result = await _controller.SetUsername(new Application.DTOs.Auth.SetUsernameRequest { Username = "!!" });

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
