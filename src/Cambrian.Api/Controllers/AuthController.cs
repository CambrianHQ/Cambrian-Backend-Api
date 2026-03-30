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
    private readonly ICreatorIdentityRepository _creators;
    private readonly UserManager<Cambrian.Domain.Entities.ApplicationUser> _userManager;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService auth, ISubscriptionRepository subscriptions, ICreatorIdentityRepository creators, UserManager<Cambrian.Domain.Entities.ApplicationUser> userManager, ILogger<AuthController> logger)
    {
        _auth = auth;
        _subscriptions = subscriptions;
        _creators = creators;
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
        AppendAuthCookie(result.Token);
        return CreatedResponse(ToSession(result));
    }

    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        _logger.LogInformation("EVENT: LoginStarted");
        var result = await _auth.LoginAsync(request);
        _logger.LogInformation("EVENT: LoginCompleted userId:{UserId} tier:{Tier}", result.UserId, result.Tier);
        AppendAuthCookie(result.Token);
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
            AppendAuthCookie(result.Token);
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
    [EnableRateLimiting("auth")]
    [HttpPost("set-password")]
    public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var hasPassword = await _userManager.HasPasswordAsync(user);
        if (hasPassword) return ErrorResponse("Password already set.");

        var result = await _userManager.AddPasswordAsync(user, request.Password);
        if (!result.Succeeded)
        {
            var msgs = new List<string>();
            foreach (var e in result.Errors) msgs.Add(e.Description);
            return ErrorResponse(string.Join("; ", msgs));
        }

        user.AuthProvider = "Local";
        await _userManager.UpdateAsync(user);

        _logger.LogInformation("EVENT: SetPassword userId:{UserId}", user.Id);
        return MessageResponse("Password set successfully.");
    }

    [Authorize]
    [EnableRateLimiting("auth")]
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

        // A user "needs a username" if their UserName is still their email (never personalized)
        var needsUsername = user is null
            || string.IsNullOrWhiteSpace(user.UserName)
            || string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase);

        // isNewUser should only be true during the very first session after registration.
        // Once a user has the Creator role (set-username promotes to Creator) or has any
        // purchase activity, they are no longer "new" even if they skipped username setup.
        var isCreatorRole = string.Equals(user?.Role, "Creator", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(user?.Role, "Admin", StringComparison.OrdinalIgnoreCase);
        var isNewUser = needsUsername && !isCreatorRole;

        var username = needsUsername ? null : user!.UserName;

        return OkResponse(new
        {
            token = freshToken ?? Request.Headers.Authorization.ToString().Replace("Bearer ", ""),
            id = profile.UserId,
            email = profile.Email,
            tier = tier.ToLowerInvariant(),
            role = profile.Role ?? "User",
            username,
            displayName = profile.DisplayName,
            phoneNumber = user?.PhoneNumber,
            isNewUser,
            needsUsername,
            canChangeUsername = needsUsername,
            creatorTier = profile.CreatorTier,
            uploadCount = profile.UploadCount,
            uploadLimit = profile.UploadLimit,
            subscriptionStatus = profile.SubscriptionStatus,
            subscriptionEndDate = profile.SubscriptionEndDate,
            platformFeePercent = profile.PlatformFeePercent,
            contractVersion = profile.ContractVersion
        });
    }

    /// <summary>
    /// Appends an HttpOnly auth cookie carrying the JWT.
    /// Cookie auth and Bearer auth are interchangeable — same token, different transport.
    /// </summary>
    private void AppendAuthCookie(string jwt)
    {
        var isProduction = HttpContext.RequestServices
            .GetRequiredService<IHostEnvironment>().IsProduction();

        Response.Cookies.Append("auth_token", jwt, new CookieOptions
        {
            HttpOnly = true,
            // Secure=true in production (HTTPS); allow HTTP in dev for local testing
            Secure   = isProduction,
            SameSite = SameSiteMode.Lax,
            Expires  = DateTimeOffset.UtcNow.AddDays(7),
            Path     = "/"
        });
    }

    private static object ToSession(AuthResponse auth) => new
    {
        token = auth.Token,
        tier = (auth.Tier ?? "free").ToLowerInvariant(),
        role = auth.Role ?? "User",
        isNewUser = auth.IsNewUser,
        user = new
        {
            id = auth.UserId.ToString(),
            email = auth.Email,
            username = auth.Username,
            phoneNumber = auth.PhoneNumber,
        }
    };

    [Authorize]
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("auth_token", new CookieOptions
        {
            HttpOnly = true,
            Secure   = true,
            SameSite = SameSiteMode.Lax,
            Path     = "/"
        });
        return MessageResponse("Logged out successfully.");
    }

    /// <summary>
    /// Refresh the JWT token. Requires a valid (non-expired or within clock-skew) token.
    /// Returns a fresh token with updated claims (role, tier, etc.).
    /// </summary>
    [Authorize]
    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshToken()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { success = false, error = "Invalid token." });

        var freshToken = await _auth.GenerateFreshTokenAsync(userId);
        if (freshToken is null)
            return NotFoundResponse("User not found.");

        _logger.LogInformation("EVENT: TokenRefreshed userId:{UserId}", userId);
        return OkResponse(new { token = freshToken });
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

        // Once a creator has chosen a username it is permanent — reject further changes.
        var alreadyHasUsername = !string.IsNullOrWhiteSpace(user.UserName)
            && !string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase);
        if (alreadyHasUsername)
            return ErrorResponse("Username cannot be changed once set.");

        // Check uniqueness via Identity UserName
        var existingByName = await _userManager.FindByNameAsync(normalized);
        if (existingByName is not null && existingByName.Id != userId)
            return ConflictResponse("That username is already taken.");

        // Also check Creators table — username must be globally unique
        var takenInCreators = await _creators.IsUsernameTakenAsync(normalized);
        if (takenInCreators)
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
            var msgs = new List<string>();
            foreach (var e in result.Errors) msgs.Add(e.Description);
            var errors = string.Join("; ", msgs);
            _logger.LogWarning("EVENT: SetUsernameFailed userId:{UserId} errors:{Errors}", userId, errors);
            return ErrorResponse(errors);
        }

        _logger.LogInformation("EVENT: UsernameSet userId:{UserId} username:{Username}", userId, normalized);

        // Sync to Creator table — create if it doesn't exist, update if it does
        try
        {
            await _creators.UpsertAsync(userId, new Cambrian.Application.DTOs.Creators.UpdateCreatorProfileRequest { Username = normalized });
            _logger.LogInformation("EVENT: CreatorUsernameSynced userId:{UserId} username:{Username}", userId, normalized);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync Creator username for userId:{UserId} — non-critical", userId);
        }

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
        // Also check Creators table — username must be globally unique
        var takenInCreators = await _creators.IsUsernameTakenAsync(normalized);
        var available = existing is null && !takenInCreators;

        return OkResponse(new { username = normalized, available });
    }

    /// <summary>
    /// CSRF token endpoint — returns a no-op token for backward compatibility.
    /// JWT Bearer auth is used for all mutating requests; CSRF tokens are unnecessary.
    /// The frontend may call this on startup — return 200 so it doesn't block auth flows.
    /// </summary>
    [HttpGet("csrf-token")]
    public IActionResult CsrfToken()
    {
        return OkResponse(new
        {
            token = "jwt-bearer-no-csrf-needed",
            note = "This API uses JWT Bearer authentication. CSRF tokens are not required."
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

        var hasUsername = user is not null
            && !string.IsNullOrWhiteSpace(user.UserName)
            && !string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase);

        return OkResponse(new
        {
            displayName = profile.DisplayName,
            username = hasUsername ? user!.UserName : null,
            canChangeUsername = !hasUsername,
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
        // C2: email is NOT changed immediately — a verification link was sent to the new address.
        return MessageResponse("Verification email sent. Check your inbox to confirm the change.");
    }

    [Authorize]
    [HttpPut("/settings/email")]
    public Task<IActionResult> UpdateEmail([FromBody] ChangeEmailRequest request) => ChangeEmail(request);

    /// <summary>
    /// Complete an email change by verifying the token from the link sent to the new address.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/auth/verify-email-change")]
    public async Task<IActionResult> VerifyEmailChange([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ErrorResponse("Verification token is required.");

        await _auth.VerifyEmailChangeAsync(token);
        return MessageResponse("Email address updated successfully.");
    }

    /// <summary>
    /// Update the current user's display name.
    /// </summary>
    [Authorize]
    [HttpPut("display-name")]
    [HttpPut("/settings/display-name")]
    public async Task<IActionResult> UpdateDisplayName([FromBody] UpdateDisplayNameRequest request)
    {
        var trimmed = request.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return ErrorResponse("Display name is required.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
            return NotFoundResponse("User not found.");

        user.DisplayName = trimmed;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var msgs = new List<string>();
            foreach (var e in result.Errors) msgs.Add(e.Description);
            return ErrorResponse(string.Join("; ", msgs));
        }

        _logger.LogInformation("EVENT: DisplayNameUpdated userId:{UserId}", userId);
        return OkResponse(new { displayName = trimmed });
    }
}
