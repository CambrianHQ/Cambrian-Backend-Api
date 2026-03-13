using System.Security.Claims;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers;

[Route("auth")]
public class AuthController : BaseController
{
    private readonly IAuthService _auth;
    private readonly ISubscriptionRepository _subscriptions;

    public AuthController(IAuthService auth, ISubscriptionRepository subscriptions)
    {
        _auth = auth;
        _subscriptions = subscriptions;
    }

    [EnableRateLimiting("auth")]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var result = await _auth.RegisterAsync(request);
        return CreatedResponse(ToSession(result));
    }

    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var result = await _auth.LoginAsync(request);
        return OkResponse(ToSession(result));
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var profile = await _auth.GetCurrentUserAsync(User);

        if (profile is null)
            return NotFoundResponse("User not found.");

        // Re-issue a fresh JWT so the frontend gets updated tier/role claims
        var freshToken = await _auth.GenerateFreshTokenAsync(profile.UserId);

        var sub = await _subscriptions.GetActiveAsync(profile.UserId);
        var tier = sub?.Plan ?? profile.Tier ?? "free";

        return OkResponse(new
        {
            token = freshToken ?? Request.Headers.Authorization.ToString().Replace("Bearer ", ""),
            user = new
            {
                id = profile.UserId,
                email = profile.Email,
                tier = tier.ToLowerInvariant(),
                role = profile.Role ?? "User"
            }
        });
    }

    private static object ToSession(AuthResponse auth) => new
    {
        token = auth.Token,
        tier = auth.Tier,
        user = new
        {
            id = auth.UserId.ToString(),
            email = auth.Email,
            tier = (auth.Tier ?? "free").ToLowerInvariant(),
            role = auth.Role ?? "User"
        }
    };

    [Authorize]
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        return MessageResponse("Logged out successfully.");
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return OkResponse(new { status = "ok", timestamp = DateTime.UtcNow });
    }

    [HttpGet("csrf-token")]
    public IActionResult GetCsrfToken()
    {
        return OkResponse(new { token = Guid.NewGuid() });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request)
    {
        await _auth.ForgotPasswordAsync(request);
        return MessageResponse("If a matching account was found, a reset code has been sent.");
    }

    [EnableRateLimiting("auth")]
    [HttpPost("verify-code")]
    public async Task<IActionResult> VerifyCode(VerifyCodeRequest request)
    {
        await _auth.VerifyCodeAsync(request);
        return MessageResponse("Code verified successfully.");
    }

    [EnableRateLimiting("auth")]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request)
    {
        await _auth.ResetPasswordAsync(request);
        return MessageResponse("Password reset successfully.");
    }

    [HttpPost("recover-username")]
    public async Task<IActionResult> RecoverUsername(RecoverUsernameRequest request)
    {
        await _auth.RecoverUsernameAsync(request);
        return MessageResponse("If a matching account was found, your username has been sent.");
    }

    [Authorize]
    [HttpGet("/settings/profile")]
    public async Task<IActionResult> GetProfile()
    {
        var profile = await _auth.GetCurrentUserAsync(User);
        return OkResponse(new
        {
            displayName = profile.DisplayName,
            email = profile.Email,
            tier = profile.Tier,
            role = profile.Role
        });
    }

    [Authorize]
    [HttpPost("/settings/password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        await _auth.ChangePasswordAsync(User, request);
        return MessageResponse("Password updated.");
    }

    [Authorize]
    [HttpPut("/settings/password")]
    public async Task<IActionResult> UpdatePassword([FromBody] ChangePasswordRequest request)
    {
        await _auth.ChangePasswordAsync(User, request);
        return MessageResponse("Password updated.");
    }

    [Authorize]
    [HttpPost("/settings/email")]
    public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest request)
    {
        await _auth.ChangeEmailAsync(User, request);
        return MessageResponse("Email updated.");
    }

    [Authorize]
    [HttpPut("/settings/email")]
    public async Task<IActionResult> UpdateEmail([FromBody] ChangeEmailRequest request)
    {
        await _auth.ChangeEmailAsync(User, request);
        return MessageResponse("Email updated.");
    }
}
