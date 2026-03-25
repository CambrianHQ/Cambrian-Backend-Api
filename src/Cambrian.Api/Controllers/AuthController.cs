using System.Security.Claims;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("auth")]
public class AuthController : BaseController
{
    private readonly IAuthService _auth;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly UserManager<Cambrian.Domain.Entities.ApplicationUser> _userManager;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService auth, ISubscriptionRepository subscriptions, UserManager<Cambrian.Domain.Entities.ApplicationUser> userManager, ILogger<AuthController> logger)
    {
        _auth = auth;
        _subscriptions = subscriptions;
        _userManager = userManager;
        _logger = logger;
    }

    [EnableRateLimiting("auth")]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        _logger.LogInformation("EVENT: RegisterStarted");
        var result = await _auth.RegisterAsync(request);
        _logger.LogInformation("EVENT: RegisterCompleted userId:{UserId} tier:{Tier}", result.UserId, result.Tier);
        return CreatedResponse(ToSession(result));
    }

    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        _logger.LogInformation("EVENT: LoginStarted");
        var result = await _auth.LoginAsync(request);
        _logger.LogInformation("EVENT: LoginCompleted userId:{UserId} tier:{Tier}", result.UserId, result.Tier);
        return OkResponse(ToSession(result));
    }

    [HttpGet("google/status")]
    public IActionResult GoogleStatus()
    {
        var clientId = _auth.GetGoogleClientId();
        var configured = !string.IsNullOrWhiteSpace(clientId);
        return Ok(new
        {
            success = true,
            data = new
            {
                configured,
                clientIdPrefix = configured ? clientId[..8] + "..." : null
            }
        });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("google")]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest request)
    {
        try
        {
            _logger.LogInformation("EVENT: GoogleLoginStarted");
            var result = await _auth.GoogleLoginAsync(request);
            _logger.LogInformation("EVENT: GoogleLoginCompleted userId:{UserId} tier:{Tier}", result.UserId, result.Tier);
            return OkResponse(ToSession(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not configured"))
        {
            _logger.LogError("EVENT: GoogleLoginFailed — {Message}", ex.Message);
            return StatusCode(503, new { success = false, error = "Google login is not available. Server misconfiguration." });
        }
        catch (Google.Apis.Auth.InvalidJwtException ex)
        {
            _logger.LogWarning("EVENT: GoogleLoginFailed — invalid token: {Message}", ex.Message);
            return Unauthorized(new { success = false, error = "Invalid Google token." });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("EVENT: GoogleLoginFailed — {Message}", ex.Message);
            return Unauthorized(new { success = false, error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("set-password")]
    public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var hasPassword = await _userManager.HasPasswordAsync(user);
        if (hasPassword) return ErrorResponse("Password already set.");

        var result = await _userManager.AddPasswordAsync(user, request.Password);
        if (!result.Succeeded)
            return ErrorResponse(string.Join("; ", result.Errors.Select(e => e.Description)));

        user.AuthProvider = "Local";
        await _userManager.UpdateAsync(user);

        _logger.LogInformation("EVENT: SetPassword userId:{UserId}", user.Id);
        return MessageResponse("Password set successfully.");
    }

    [Authorize]
    [HttpPost("link-google")]
    public async Task<IActionResult> LinkGoogle([FromBody] GoogleLoginRequest request)
    {
        try
        {
            var payload = await Google.Apis.Auth.GoogleJsonWebSignature.ValidateAsync(
                request.IdToken,
                new Google.Apis.Auth.GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[] { _auth.GetGoogleClientId() }
                });

            var user = await _userManager.GetUserAsync(User);
            if (user is null) return Unauthorized();

            if (!string.IsNullOrEmpty(user.GoogleId))
                return ErrorResponse("Google account already linked.");

            // Verify the Google email matches the user's email
            if (!string.Equals(user.Email, payload.Email, StringComparison.OrdinalIgnoreCase))
                return ErrorResponse("Google account email does not match your account email.");

            user.GoogleId = payload.Subject;
            await _userManager.UpdateAsync(user);

            _logger.LogInformation("EVENT: LinkGoogle userId:{UserId}", user.Id);
            return MessageResponse("Google account linked successfully.");
        }
        catch (Google.Apis.Auth.InvalidJwtException)
        {
            return Unauthorized();
        }
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var profile = await _auth.GetCurrentUserAsync(User);

        if (profile is null)
        {
            _logger.LogWarning("EVENT: MeFailed — user not found in database");
            return NotFoundResponse("User not found.");
        }

        // Re-issue a fresh JWT so the frontend gets updated tier/role claims
        var freshToken = await _auth.GenerateFreshTokenAsync(profile.UserId);

        var sub = await _subscriptions.GetActiveAsync(profile.UserId);
        var tier = sub?.Plan
            ?? (string.Equals(profile.CreatorTier, "Pro", StringComparison.OrdinalIgnoreCase) ? "pro" : "free");

        _logger.LogInformation(
            "EVENT: MeResolved userId:{UserId} profileTier:{ProfileTier} subscriptionPlan:{SubPlan} resolvedTier:{ResolvedTier}",
            profile.UserId, profile.Tier, sub?.Plan, tier.ToLowerInvariant());

        return OkResponse(new
        {
            token = freshToken ?? Request.Headers.Authorization.ToString().Replace("Bearer ", ""),
            user = new
            {
                id = profile.UserId,
                email = profile.Email,
                tier = tier.ToLowerInvariant(),
                role = profile.Role ?? "User",
                creatorTier = profile.CreatorTier,
                uploadCount = profile.UploadCount,
                uploadLimit = profile.UploadLimit,
                subscriptionStatus = profile.SubscriptionStatus,
                subscriptionEndDate = profile.SubscriptionEndDate,
                platformFeePercent = profile.PlatformFeePercent,
                contractVersion = profile.ContractVersion
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

    // REMOVED: csrf-token endpoint was returning unvalidated random GUIDs
    // providing false security. JWT Bearer auth mitigates CSRF inherently.

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
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var profile = await _auth.GetCurrentUserAsync(User);
        // Read images from AspNetUsers — the source of truth written by PATCH /users/me.
        // CreatorProfile.ProfileImageUrl is a separate creator storefront image.
        var user = await _userManager.FindByIdAsync(userId);

        var currentTierConfig = TierManifest.For(user?.CreatorTier ?? CreatorTier.Free);
        var canUpgrade = currentTierConfig.Tier != CreatorTier.Pro;
        var proTier = TierManifest.Pro;

        return OkResponse(new
        {
            displayName = profile.DisplayName,
            email = profile.Email,
            tier = profile.Tier,
            role = profile.Role,
            bio = user?.Bio,
            profileImageUrl = user?.ProfileImageUrl,
            coverImageUrl = user?.CoverImageUrl,
            subscription = new
            {
                creatorTier = currentTierConfig.DisplayName,
                status = profile.SubscriptionStatus,
                expiresAt = profile.SubscriptionEndDate,
                uploadCount = profile.UploadCount,
                uploadLimit = currentTierConfig.UploadLimit,
                platformFeePercent = currentTierConfig.FeeRate,
                canUpgrade,
                upgrade = canUpgrade ? new
                {
                    tier = proTier.Slug,
                    displayName = proTier.DisplayName,
                    priceCents = proTier.PriceCents,
                    features = proTier.Features,
                    checkoutEndpoint = "/billing/checkout"
                } : null
            }
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
    public Task<IActionResult> UpdatePassword([FromBody] ChangePasswordRequest request) => ChangePassword(request);

    [Authorize]
    [HttpPost("/settings/email")]
    public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest request)
    {
        await _auth.ChangeEmailAsync(User, request);
        return MessageResponse("Email updated.");
    }

    [Authorize]
    [HttpPut("/settings/email")]
    public Task<IActionResult> UpdateEmail([FromBody] ChangeEmailRequest request) => ChangeEmail(request);
}
