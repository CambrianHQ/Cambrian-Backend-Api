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

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var user = await _userManager.FindByIdAsync(userId);
        var isNewUser = user is null
            || string.IsNullOrWhiteSpace(user.UserName)
            || string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase);
        var username = isNewUser ? null : user!.UserName;

        return OkResponse(new
        {
            token = freshToken ?? Request.Headers.Authorization.ToString().Replace("Bearer ", ""),
            isNewUser,
            user = new
            {
                id = profile.UserId,
                email = profile.Email,
                tier = tier.ToLowerInvariant(),
                role = profile.Role ?? "User",
                username,
                displayName = profile.DisplayName,
                isNewUser,
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
        isNewUser = auth.IsNewUser,
        user = new
        {
            id = auth.UserId.ToString(),
            email = auth.Email,
            tier = (auth.Tier ?? "free").ToLowerInvariant(),
            role = auth.Role ?? "User",
            username = auth.Username,
            isNewUser = auth.IsNewUser
        }
    };

    [Authorize]
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        return MessageResponse("Logged out successfully.");
    }

    /// <summary>
    /// Set or update the current user's username during onboarding.
    /// Does not require Creator tier — available to any authenticated user.
    /// </summary>
    [Authorize]
    [HttpPost("set-username")]
    public async Task<IActionResult> SetUsername([FromBody] SetUsernameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
            return ErrorResponse("Username is required.");

        var normalized = request.Username.Trim().ToLowerInvariant();

        if (normalized.Length < 3 || normalized.Length > 40)
            return ErrorResponse("Username must be between 3 and 40 characters.");

        // Only allow alphanumeric, hyphens, underscores
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^[a-z0-9_-]+$"))
            return ErrorResponse("Username may only contain letters, numbers, hyphens, and underscores.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
            return NotFoundResponse("User not found.");

        // Check uniqueness via Identity UserName
        var existingByName = await _userManager.FindByNameAsync(normalized);
        if (existingByName is not null && existingByName.Id != userId)
            return ConflictResponse("That username is already taken.");

        user.UserName = normalized;
        user.NormalizedUserName = normalized.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(user.DisplayName))
            user.DisplayName = request.Username.Trim(); // preserve original casing for display

        // Promote to Creator role on username set — this is the onboarding completion step.
        // Users who set a username are entering the creator flow and need access to
        // creator-profile, payout, and upload endpoints.
        if (string.Equals(user.Role, "User", StringComparison.OrdinalIgnoreCase))
        {
            user.Role = "Creator";
            _logger.LogInformation("EVENT: RolePromoted userId:{UserId} from=User to=Creator (username onboarding)", userId);
        }

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("EVENT: SetUsernameFailed userId:{UserId} errors:{Errors}", userId, errors);
            return ErrorResponse(errors);
        }

        _logger.LogInformation("EVENT: UsernameSet userId:{UserId} username:{Username}", userId, normalized);

        // Return a fresh JWT with updated role/username claims
        var freshToken = await _auth.GenerateFreshTokenAsync(userId);

        return OkResponse(new
        {
            username = normalized,
            displayName = user.DisplayName,
            role = user.Role,
            token = freshToken
        });
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return OkResponse(new { status = "ok", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Check username availability during onboarding. Public — no auth required.
    /// Mirrors /api/creators/username-availability but accessible at the /auth path
    /// so the onboarding flow does not depend on the creator routes.
    /// </summary>
    [EnableRateLimiting("auth")]
    [HttpGet("username-availability")]
    public async Task<IActionResult> CheckUsernameAvailability([FromQuery] string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return ErrorResponse("Username is required.");

        var normalized = username.Trim().ToLowerInvariant();

        if (normalized.Length < 3 || normalized.Length > 40)
            return OkResponse(new { username = normalized, available = false, reason = "Username must be between 3 and 40 characters." });

        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^[a-z0-9_-]+$"))
            return OkResponse(new { username = normalized, available = false, reason = "Username may only contain letters, numbers, hyphens, and underscores." });

        var existing = await _userManager.FindByNameAsync(normalized);
        var available = existing is null;

        return OkResponse(new { username = normalized, available });
    }

    /// <summary>
    /// CSRF token endpoint — intentionally retired.
    /// JWT Bearer auth is used for all mutating requests; CSRF tokens are unnecessary.
    /// Returns 410 Gone so the frontend can distinguish "retired" from "missing".
    /// </summary>
    [HttpGet("csrf-token")]
    public IActionResult CsrfToken()
    {
        return StatusCode(410, new
        {
            success = false,
            error = "CSRF tokens are not used. This API uses JWT Bearer authentication which is not vulnerable to CSRF attacks.",
            retired = true
        });
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

    [EnableRateLimiting("auth")]
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
