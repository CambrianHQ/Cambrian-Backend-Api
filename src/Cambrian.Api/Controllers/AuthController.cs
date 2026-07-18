using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Application.Auth;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Validation;
using Cambrian.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("auth")]
public class AuthController : BaseController
{
    private readonly IAuthService _auth;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ICapabilityResolver _capabilities;
    private readonly UserManager<Cambrian.Domain.Entities.ApplicationUser> _userManager;
    private readonly ITransactionManager _tx;
    private readonly ILogger<AuthController> _logger;

    private static readonly HashSet<string> _reservedUsernames = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "api", "www", "support", "help", "mail", "blog", "app",
        "creator", "cambrian", "marketplace", "verify", "press", "business",
        "developers", "embed", "sync", "pricing", "about"
    };

    private readonly ICreatorProfileRepository _profiles;

    public AuthController(IAuthService auth, ISubscriptionRepository subscriptions, ICreatorIdentityRepository creators, ICreatorProfileRepository profiles, ICapabilityResolver capabilities, UserManager<Cambrian.Domain.Entities.ApplicationUser> userManager, ITransactionManager tx, ILogger<AuthController> logger)
    {
        _auth = auth;
        _subscriptions = subscriptions;
        _creators = creators;
        _profiles = profiles;
        _capabilities = capabilities;
        _userManager = userManager;
        _tx = tx;
        _logger = logger;
    }

    [EnableRateLimiting("auth")]
    [HttpPost("register")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        _logger.LogInformation("EVENT: RegisterStarted");
        var result = await _auth.RegisterAsync(request);
        _logger.LogInformation("EVENT: RegisterCompleted userId:{UserId} tier:{Tier}", result.UserId, result.Tier);
        AppendAuthCookie(result.Token);
        var caps = await ResolveCapabilitiesAsync(result.UserId.ToString());
        return CreatedResponse(ToSession(result, caps));
    }

    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        _logger.LogInformation("EVENT: LoginStarted");
        var result = await _auth.LoginAsync(request);
        _logger.LogInformation("EVENT: LoginCompleted userId:{UserId} tier:{Tier}", result.UserId, result.Tier);
        AppendAuthCookie(result.Token);
        var caps = await ResolveCapabilitiesAsync(result.UserId.ToString());
        return OkResponse(ToSession(result, caps));
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
            var caps = await ResolveCapabilitiesAsync(result.UserId.ToString());
            return OkResponse(ToSession(result, caps));
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

        var isAdminRole = string.Equals(user?.Role, "Admin", StringComparison.OrdinalIgnoreCase);

        // A non-admin user "needs a username" if their UserName is still their
        // email sentinel. Admins are operational accounts and must not be forced
        // through creator username onboarding.
        var hasUsername = user is not null && UsernameHelper.IsSet(user);
        var needsUsername = user is null || (!isAdminRole && !hasUsername);

        // isNewUser should only be true during the very first session after registration.
        // Once a user has the Creator role (set-username promotes to Creator) or has any
        // purchase activity, they are no longer "new" even if they skipped username setup.
        var isCreatorRole = string.Equals(user?.Role, "Creator", StringComparison.OrdinalIgnoreCase)
                         || isAdminRole;
        var isNewUser = needsUsername && !isCreatorRole;

        var username = hasUsername ? user!.UserName : null;

        // Resolve capabilities for this user
        var capabilities = user is not null
            ? await _capabilities.ResolveAsync(user)
            : Array.Empty<string>();

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
            requiresUsernameSetup = needsUsername,
            canChangeUsername = needsUsername && !isAdminRole,
            creatorTier = profile.CreatorTier,
            uploadCount = profile.UploadCount,
            uploadLimit = profile.UploadLimit,
            subscriptionStatus = profile.SubscriptionStatus,
            subscriptionEndDate = profile.SubscriptionEndDate,
            platformFeePercent = profile.PlatformFeePercent,
            contractVersion = profile.ContractVersion,
            // Account credential state — the frontend gates the Change Password / Change Email
            // forms on these. Mirrors what /auth/login and /auth/register already return.
            hasPassword = !string.IsNullOrEmpty(user?.PasswordHash),
            googleLinked = !string.IsNullOrEmpty(user?.GoogleId),
            // Verification + signup timestamp — the frontend surfaces a verification
            // banner (Studio/Upload) from emailVerified instead of discovering the
            // VerifiedEmail 403 at publish time, and uses createdAt for analytics
            // (signup_date person property, minutes_from_signup on upload milestones).
            emailVerified = user?.EmailVerified ?? false,
            createdAt = user?.CreatedAt,
            capabilities
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

        Response.Cookies.Append("auth_token", jwt, BuildAuthCookieOptions(
            isProduction, DateTimeOffset.UtcNow.AddDays(7)));
    }

    /// <summary>
    /// Production login is a cross-site fetch (cambrianmusic.com → the API host):
    /// browsers refuse to STORE a Lax cookie from a cross-site response, which
    /// silently broke every navigation-based authorized flow (e.g. mastered-download
    /// redirects). None+Secure is storable cross-site and still first-party on
    /// top-level navigations to this host; unsafe methods stay guarded by
    /// CookieCsrfProtectionMiddleware. Dev keeps Lax (HTTP cannot carry None+Secure).
    /// </summary>
    public static CookieOptions BuildAuthCookieOptions(bool isProduction, DateTimeOffset? expires = null)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure   = isProduction,
            SameSite = isProduction ? SameSiteMode.None : SameSiteMode.Lax,
            Expires  = expires,
            Path     = "/"
        };
    }

    private static object ToSession(AuthResponse auth, IReadOnlyList<string> capabilities)
    {
        var role = auth.Role ?? "User";
        var requiresUsernameSetup = auth.Username == null
            && !string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);

        return new
        {
            token = auth.Token,
            tier = (auth.Tier ?? "free").ToLowerInvariant(),
            role,
            isNewUser = auth.IsNewUser && requiresUsernameSetup,
            needsUsername = requiresUsernameSetup,
            requiresUsernameSetup,
            user = new
            {
                id = auth.UserId.ToString(),
                email = auth.Email,
                username = auth.Username,
                displayName = auth.DisplayName,
                phoneNumber = auth.PhoneNumber,
            },
            capabilities
        };
    }

    /// <summary>
    /// Loads the user by id and resolves their capability set. Returns an empty
    /// array if the user is no longer resolvable (frontend treats missing as 401).
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveCapabilitiesAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return Array.Empty<string>();
        return await _capabilities.ResolveAsync(user);
    }

    [Authorize]
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        var isProduction = HttpContext.RequestServices
            .GetRequiredService<IHostEnvironment>().IsProduction();

        // Delete attributes must match Append's or the browser keeps the cookie.
        Response.Cookies.Delete("auth_token", BuildAuthCookieOptions(isProduction));
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

        if (_reservedUsernames.Contains(normalized))
            return ErrorResponse("That username is reserved.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
            return NotFoundResponse("User not found.");

        // Once a creator has chosen a username it is permanent — reject further changes.
        if (UsernameHelper.IsSet(user))
            return ErrorResponse("Username cannot be changed once set.");

        // Choosing a username is a generic onboarding step available to ANY authenticated
        // account (listeners included) — it must NOT change the user's role. Previously this
        // endpoint silently promoted every "User" to "Creator", which both granted creator
        // capabilities and provisioned a public storefront for plain listeners. A listener
        // stays a listener; an account becomes a creator only through an explicit path
        // (registration with role=creator, admin promotion, or admin/billing tier upgrade).
        var isCreatorAccount = string.Equals(user.Role, "Creator", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase);

        // Wrap ALL uniqueness checks + writes in a transaction so concurrent requests
        // cannot both pass checks and commit duplicate usernames.
        await using var transaction = await _tx.BeginTransactionAsync();
        try
        {
            // Check uniqueness via Identity UserName (inside transaction)
            var existingByName = await _userManager.FindByNameAsync(normalized);
            if (existingByName is not null && existingByName.Id != userId)
            {
                await _tx.RollbackAsync();
                return ConflictResponse("That username is already taken.");
            }

            // Also check Creators table — username must be globally unique
            var takenInCreators = await _creators.IsUsernameTakenAsync(normalized);
            if (takenInCreators)
            {
                await _tx.RollbackAsync();
                return ConflictResponse("That username is already taken.");
            }

            user.UserName = normalized;
            user.NormalizedUserName = normalized.ToUpperInvariant();
            // Preserve original casing for display; pass to Creator row too (BUG #4 fix)
            var displayName = user.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = MetadataSanitizer.NormalizeRequired(request.Username, "Display name");
                user.DisplayName = displayName;
            }

            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                await _tx.RollbackAsync();
                var msgs = new List<string>();
                foreach (var e in result.Errors) msgs.Add(e.Description);
                var errors = string.Join("; ", msgs);
                _logger.LogWarning("EVENT: SetUsernameFailed userId:{UserId} errors:{Errors}", userId, errors);
                return ErrorResponse(errors);
            }

            _logger.LogInformation("EVENT: UsernameSet userId:{UserId} username:{Username}", userId, normalized);

            // Creator artifacts (the Creators row that powers /creator/username/{slug} and
            // the public storefront profile) are only provisioned for accounts that are
            // actually creators. Provisioning them for a listener would make the listener
            // publicly discoverable as a creator even though no public creator lookup filters
            // on role.
            if (isCreatorAccount)
            {
                // Pass DisplayName so Creator row gets the human-readable name, not just the normalized username
                await _creators.UpsertAsync(userId, new Cambrian.Application.DTOs.Creators.UpdateCreatorProfileRequest
                {
                    Username = normalized,
                    DisplayName = displayName
                });
                _logger.LogInformation("EVENT: CreatorUsernameSynced userId:{UserId} username:{Username}", userId, normalized);

                // Auto-provision CreatorProfile so the storefront, collections, and
                // /creator/username/{slug} endpoints work immediately after registration
                // without requiring a separate creatorProfileApi.upsert() call.
                try
                {
                    var existingProfile = await _profiles.GetByUserIdAsync(userId);
                    if (existingProfile is null)
                    {
                        await _profiles.UpsertAsync(userId, normalized, "", null, null, false, true);
                        _logger.LogInformation("EVENT: CreatorProfileProvisioned userId:{UserId} slug:{Slug}", userId, normalized);
                    }
                }
                catch (Exception profileEx)
                {
                    // Non-critical — log but don't fail the set-username transaction
                    _logger.LogWarning(profileEx, "CreatorProfile auto-provision failed for userId={UserId}; user can create it manually", userId);
                }
            }

            await _tx.CommitAsync();
        }
        catch (DbUpdateException dbEx) when (
            dbEx.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true ||
            dbEx.InnerException?.Message.Contains("23505", StringComparison.Ordinal) == true)
        {
            await _tx.RollbackAsync();
            _logger.LogWarning("EVENT: CreatorUsernameConflict userId:{UserId} username:{Username} — DB unique violation", userId, normalized);
            return ConflictResponse("That username is already taken.");
        }
        catch (Exception ex)
        {
            await _tx.RollbackAsync();
            _logger.LogError(ex, "EVENT: SetUsernameFailed userId:{UserId} — rolling back Identity + Creator changes", userId);
            return ErrorResponse("Failed to complete username setup. Please try again.");
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

        if (_reservedUsernames.Contains(normalized))
            return OkResponse(new { username = normalized, available = false, reason = "That username is reserved." });

        var existing = await _userManager.FindByNameAsync(normalized);
        // Also check Creators table — username must be globally unique
        var takenInCreators = await _creators.IsUsernameTakenAsync(normalized);
        var available = existing is null && !takenInCreators;

        return OkResponse(new { username = normalized, available });
    }

    /// <summary>
    /// Issues a real antiforgery request token and stores the matching HttpOnly
    /// antiforgery cookie. Cookie-authenticated mutations must echo this token in
    /// the X-CSRF-TOKEN header.
    /// </summary>
    [HttpGet("csrf-token")]
    public IActionResult CsrfToken([FromServices] IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return OkResponse(new
        {
            token = tokens.RequestToken,
            headerName = "X-CSRF-TOKEN"
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
    /// Re-send the initial email verification link to the authenticated user's email.
    /// (The first link is sent automatically on registration.)
    /// </summary>
    [Authorize]
    [EnableRateLimiting("auth")]
    [HttpPost("send-verification-email")]
    public async Task<IActionResult> SendVerificationEmail()
    {
        try
        {
            await _auth.SendEmailVerificationAsync(User);
            return MessageResponse("Verification email sent.");
        }
        catch (InvalidOperationException ex)
        {
            return ErrorResponse(ex.Message);
        }
        catch (VerificationEmailDeliveryException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                error = new
                {
                    code = "verification_email_failed",
                    message = ex.Message,
                    correlationId = HttpContext.TraceIdentifier,
                }
            });
        }
    }

    /// <summary>
    /// Complete the initial email verification by validating the link token.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/auth/verify-email")]
    public async Task<IActionResult> VerifyEmail([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ErrorResponse("Verification token is required.");

        try
        {
            await _auth.VerifyEmailAsync(token);
            return MessageResponse("Email verified successfully. You can now access all features.");
        }
        catch (InvalidOperationException ex)
        {
            return ErrorResponse(ex.Message);
        }
    }

    /// <summary>
    /// Update the current user's display name.
    /// </summary>
    [Authorize]
    [HttpPut("display-name")]
    [HttpPut("/settings/display-name")]
    public async Task<IActionResult> UpdateDisplayName([FromBody] UpdateDisplayNameRequest request)
    {
        string trimmed;
        try
        {
            trimmed = MetadataSanitizer.NormalizeRequired(request.DisplayName, "Display name");
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.DataAnnotations.ValidationException)
        {
            return ErrorResponse(ex.Message);
        }

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

        // Sync to Creator table so storefront/tracks show the updated name
        var creatorId = await _creators.GetCreatorIdForUserAsync(userId);
        if (creatorId.HasValue)
        {
            await _creators.UpsertAsync(userId, new Cambrian.Application.DTOs.Creators.UpdateCreatorProfileRequest
            {
                DisplayName = trimmed
            });
        }

        _logger.LogInformation("EVENT: DisplayNameUpdated userId:{UserId}", userId);
        return OkResponse(new { displayName = trimmed });
    }
}
