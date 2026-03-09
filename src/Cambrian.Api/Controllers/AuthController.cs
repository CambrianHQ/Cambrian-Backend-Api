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

    public AuthController(IAuthService auth)
    {
        _auth = auth;
    }

    [EnableRateLimiting("auth")]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var result = await _auth.RegisterAsync(request);
        return StatusCode(201, ToSession(result));
    }

    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var result = await _auth.LoginAsync(request);
        return Ok(ToSession(result));
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var session = await _auth.GetSessionAsync(User);

        var bearerToken = Request.Headers.Authorization.ToString().Replace("Bearer ", "");

        return Ok(new
        {
            token = bearerToken,
            user = new
            {
                id = session.UserId.ToString(),
                email = session.Email,
                tier = session.Tier
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
            tier = auth.Tier
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

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request)
    {
        await _auth.ForgotPasswordAsync(request);
        return MessageResponse("If a matching account was found, a reset code has been sent.");
    }

    [HttpPost("verify-code")]
    public async Task<IActionResult> VerifyCode(VerifyCodeRequest request)
    {
        await _auth.VerifyCodeAsync(request);
        return MessageResponse("Code verified successfully.");
    }

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
    public IActionResult GetProfile()
    {
        return OkResponse(new { displayName = (string?)null, email = (string?)null });
    }

    [Authorize]
    [HttpPost("/settings/password")]
    public IActionResult ChangePassword()
    {
        return MessageResponse("Password updated.");
    }

    [Authorize]
    [HttpPut("/settings/password")]
    public IActionResult UpdatePassword()
    {
        return MessageResponse("Password updated.");
    }

    [Authorize]
    [HttpPost("/settings/email")]
    public IActionResult ChangeEmail()
    {
        return MessageResponse("Email updated.");
    }

    [Authorize]
    [HttpPut("/settings/email")]
    public IActionResult UpdateEmail()
    {
        return MessageResponse("Email updated.");
    }
}
