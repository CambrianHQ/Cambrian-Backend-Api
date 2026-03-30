using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Cambrian.Application.Services;

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly JwtSettings _jwtSettings;
    private readonly GoogleSettings _googleSettings;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IEmailService _email;
    private readonly ISmsService _sms;
    private readonly ILogger<AuthService> _logger;

    /// <summary>How long a password reset code is valid.</summary>
    private static readonly TimeSpan ResetCodeLifetime = TimeSpan.FromMinutes(15);

    private const string InvalidOrExpiredCode = "Invalid or expired code.";

    public AuthService(
        UserManager<ApplicationUser> users,
        IOptions<JwtSettings> jwtOptions,
        IOptions<GoogleSettings> googleOptions,
        ISubscriptionRepository subscriptions,
        IEmailService email,
        ISmsService sms,
        ILogger<AuthService> logger)
    {
        _users = users;
        _jwtSettings = jwtOptions.Value;
        _googleSettings = googleOptions.Value;
        _subscriptions = subscriptions;
        _email = email;
        _sms = sms;
        _logger = logger;

        var hasClientId = !string.IsNullOrWhiteSpace(_googleSettings.ClientId);
        _logger.LogInformation("Google OAuth configured: {Configured}", hasClientId);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        var user = await _users.FindByEmailAsync(request.Email);

        if (user is null)
        {
            _logger.LogWarning("Login failed: no account for {Email}", request.Email);
            throw new UnauthorizedAccessException("Invalid credentials");
        }

        var valid = await _users.CheckPasswordAsync(user, request.Password);

        if (!valid)
        {
            _logger.LogWarning("Login failed: bad password for {UserId}", user.Id);
            throw new UnauthorizedAccessException("Invalid credentials");
        }

        var sub = await _subscriptions.GetActiveAsync(user.Id);
        var resolvedTier = sub?.Plan ?? (user.CreatorTier == CreatorTier.Pro ? "pro" : "free");

        var token = GenerateJwt(user);

        // A user "needs a username" if their UserName is still their email (never personalized)
        var needsUsername = string.IsNullOrWhiteSpace(user.UserName)
                     || string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase);

        // isNewUser should only be true during the initial onboarding window.
        // Creators (set-username promotes to Creator) and Admins are never "new".
        var isCreatorOrAdmin = string.Equals(user.Role, "Creator", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase);
        var isNewUser = needsUsername && !isCreatorOrAdmin;

        _logger.LogInformation("Login success: User={UserId} Tier={Tier} IsNew={IsNew}", user.Id, resolvedTier, isNewUser);

        return new AuthResponse
        {
            UserId = Guid.Parse(user.Id),
            Email = user.Email ?? "",
            Token = token,
            Tier = resolvedTier.ToLowerInvariant(),
            Role = user.Role ?? "User",
            Username = needsUsername ? null : user.UserName,
            PhoneNumber = user.PhoneNumber,
            IsNewUser = isNewUser
        };
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
    {
        var isCreator = string.Equals(request.Role, "creator", StringComparison.OrdinalIgnoreCase);
        var user = new ApplicationUser
        {
            Email = request.Email,
            UserName = request.Email,
            DisplayName = request.DisplayName ?? request.Email.Split('@')[0],
            Tier = "free",
            Role = isCreator ? "Creator" : "User",
            CreatorTier = CreatorTier.Free,
            PhoneNumber = request.PhoneNumber
        };

        var result = await _users.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("Registration failed: {Email} — {Errors}", request.Email, errors);
            throw new InvalidOperationException($"Registration failed: {errors}");
        }

        _logger.LogInformation("Registration success: User={UserId} Email={Email}", user.Id, user.Email);
        var token = GenerateJwt(user);

        return new AuthResponse
        {
            UserId = Guid.Parse(user.Id),
            Email = user.Email ?? "",
            Token = token,
            Tier = user.Tier,
            Role = user.Role,
            Username = null,
            PhoneNumber = user.PhoneNumber,
            IsNewUser = true  // just registered, no custom username yet
        };
    }

    public async Task<UserProfileResponse> GetCurrentUserAsync(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

        if (userId is null)
            throw new UnauthorizedAccessException("No user identity found");

        var user = await _users.FindByIdAsync(userId)
                   ?? throw new UnauthorizedAccessException("User not found");

        var tierConfig = TierManifest.For(user.CreatorTier);
        return new UserProfileResponse
        {
            UserId = user.Id,
            Email = user.Email ?? "",
            DisplayName = user.DisplayName ?? string.Empty,
            Role = user.Role,
            Tier = user.Tier,
            VerifiedCreator = user.VerifiedCreator,
            CreatorTier = user.CreatorTier.ToString(),
            UploadCount = user.UploadCount,
            UploadLimit = tierConfig.UploadLimit,
            SubscriptionStatus = user.SubscriptionStatus,
            SubscriptionEndDate = user.SubscriptionEndDate,
            PlatformFeePercent = tierConfig.FeeRate,
            ContractVersion = TierManifest.ContractVersion
        };
    }

    public async Task<AuthResponse> GetSessionAsync(ClaimsPrincipal principal)
    {
        var profile = await GetCurrentUserAsync(principal);
        var sub = await _subscriptions.GetActiveAsync(profile.UserId);
        var tier = sub?.Plan
            ?? (string.Equals(profile.CreatorTier, "Pro", StringComparison.OrdinalIgnoreCase) ? "pro" : "free");

        var user = await _users.FindByIdAsync(profile.UserId)
                   ?? throw new UnauthorizedAccessException("User not found");
        var token = GenerateJwt(user);

        return new AuthResponse
        {
            UserId = Guid.Parse(profile.UserId),
            Email = profile.Email,
            Token = token,
            Tier = tier.ToLowerInvariant(),
            Role = profile.Role ?? "User",
            PhoneNumber = user.PhoneNumber
        };
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request)
    {
        var usePhone = !string.IsNullOrWhiteSpace(request.PhoneNumber);
        var useEmail = !string.IsNullOrWhiteSpace(request.Email);

        if (!useEmail && !usePhone)
            return; // silent - do not reveal whether the contact exists

        var user = useEmail
            ? await _users.FindByEmailAsync(request.Email!)
            : await _users.Users.FirstOrDefaultAsync(u => u.PhoneNumber == request.PhoneNumber);

        if (user is null)
            return; // silent

        // Generate a cryptographically random 8-character alphanumeric code
        // (32^8 = ~1.1 trillion combinations vs 900K for 6-digit numeric)
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // excludes ambiguous chars: 0/O, 1/I
        var codeChars = new char[8];
        for (var i = 0; i < codeChars.Length; i++)
            codeChars[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        var code = new string(codeChars);

        user.PasswordResetCode = HashResetCode(code);
        user.PasswordResetCodeExpiry = DateTime.UtcNow.Add(ResetCodeLifetime);
        await _users.UpdateAsync(user);

        try
        {
            if (usePhone)
                await _sms.SendPasswordResetAsync(request.PhoneNumber!, code);
            else
                await _email.SendPasswordResetAsync(user.Email!, code);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send password reset to user {UserId}", user.Id);
        }
    }

    public async Task VerifyCodeAsync(VerifyCodeRequest request)
    {
        var user = await FindUserByContact(request.Email, request.PhoneNumber);
        if (user is null)
            throw new InvalidOperationException(InvalidOrExpiredCode);

        ValidateResetCode(user, request.Code);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request)
    {
        var user = await FindUserByContact(request.Email, request.PhoneNumber);
        if (user is null)
            throw new InvalidOperationException(InvalidOrExpiredCode);

        ValidateResetCode(user, request.Code);

        // Code is valid - perform the actual password reset via Identity
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, request.NewPassword);

        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Password reset failed: {errors}");
        }

        // Invalidate the code so it cannot be reused
        user.PasswordResetCode = null;
        user.PasswordResetCodeExpiry = null;
        await _users.UpdateAsync(user);
    }

    public async Task RecoverUsernameAsync(RecoverUsernameRequest request)
    {
        var usePhone = !string.IsNullOrWhiteSpace(request.PhoneNumber);
        var useEmail = !string.IsNullOrWhiteSpace(request.Email);

        if (!useEmail && !usePhone)
            return; // silent - do not reveal whether the contact exists

        var user = useEmail
            ? await _users.FindByEmailAsync(request.Email!)
            : await _users.Users.FirstOrDefaultAsync(u => u.PhoneNumber == request.PhoneNumber);

        if (user is null)
            return; // silent

        var displayName = user.DisplayName ?? "(not set)";

        if (usePhone)
        {
            await _sms.SendAsync(request.PhoneNumber!, $"Your Cambrian display name is: {displayName}");
        }
        else
        {
            await _email.SendAsync(
                user.Email!,
                "Cambrian - Your Username",
                $"<p>Your display name is: <strong>{displayName}</strong></p>");
        }
    }

    public async Task ChangePasswordAsync(ClaimsPrincipal principal, ChangePasswordRequest request)
    {
        var user = await GetRequiredUser(principal);
        var result = await _users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);

        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Password change failed: {errors}");
        }
    }

    /// <summary>
    /// Phase A — does NOT change the email immediately.
    /// Stores a pending change and sends a verification link to the new address.
    /// The live Email field is only updated when <see cref="VerifyEmailChangeAsync"/> is called.
    /// </summary>
    public async Task ChangeEmailAsync(ClaimsPrincipal principal, ChangeEmailRequest request)
    {
        var user = await GetRequiredUser(principal);

        // Verify the current password before storing the pending change
        var passwordValid = await _users.CheckPasswordAsync(user, request.Password);
        if (!passwordValid)
            throw new UnauthorizedAccessException("Invalid password.");

        // Check that the new email is not already claimed
        var existing = await _users.FindByEmailAsync(request.NewEmail);
        if (existing is not null && existing.Id != user.Id)
            throw new InvalidOperationException("Email is already in use.");

        // Generate a secure random 32-byte token, URL-safe base64 encoded.
        // Embed the userId so VerifyEmailChangeAsync can look up the user with FindByIdAsync
        // rather than scanning Users with EF async LINQ (which requires IAsyncQueryProvider).
        var rawBytes = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var plaintext = $"{user.Id}.{rawBytes}";
        var tokenHash = HashResetCode(plaintext); // SHA-256 hex, same helper as password reset

        user.PendingEmail = request.NewEmail;
        user.EmailChangeToken = tokenHash;
        user.EmailChangeTokenExpiry = DateTime.UtcNow.AddHours(24);
        await _users.UpdateAsync(user);

        // Notify old address first (account takeover defense)
        var oldEmail = user.Email ?? user.UserName ?? string.Empty;
        if (!string.IsNullOrEmpty(oldEmail))
            await _email.SendEmailChangeNotificationAsync(oldEmail, request.NewEmail);

        // Send verification link to the new address
        var link = $"/auth/verify-email-change?token={Uri.EscapeDataString(plaintext)}";
        await _email.SendEmailChangeVerificationAsync(request.NewEmail, link);
    }

    /// <inheritdoc />
    public async Task VerifyEmailChangeAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Invalid or expired email change token.");

        // Token format: "{userId}.{randomBytes}" — extract userId to avoid EF async LINQ scan.
        var dotIndex = token.IndexOf('.');
        if (dotIndex <= 0)
            throw new InvalidOperationException("Invalid or expired email change token.");

        var userId = token[..dotIndex];
        var tokenHash = HashResetCode(token);

        var user = await _users.FindByIdAsync(userId);

        if (user is null
            || user.EmailChangeToken != tokenHash
            || user.EmailChangeTokenExpiry is null
            || user.EmailChangeTokenExpiry <= DateTime.UtcNow
            || user.PendingEmail is null)
            throw new InvalidOperationException("Invalid or expired email change token.");

        // Swap in the pending email
        var newEmail = user.PendingEmail!;
        user.Email = newEmail;
        user.UserName = newEmail;   // keep UserName in sync
        user.NormalizedEmail = newEmail.ToUpperInvariant();
        user.NormalizedUserName = newEmail.ToUpperInvariant();

        // Clear pending state
        user.PendingEmail = null;
        user.EmailChangeToken = null;
        user.EmailChangeTokenExpiry = null;

        await _users.UpdateAsync(user);
    }

    // -- Helpers --

    private async Task<ApplicationUser> GetRequiredUser(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

        if (userId is null)
            throw new UnauthorizedAccessException("No user identity found");

        return await _users.FindByIdAsync(userId)
               ?? throw new UnauthorizedAccessException("User not found");
    }

    private async Task<ApplicationUser?> FindUserByContact(string? email, string? phoneNumber = null)
    {
        if (!string.IsNullOrWhiteSpace(email))
            return await _users.FindByEmailAsync(email);
        if (!string.IsNullOrWhiteSpace(phoneNumber))
            return await _users.Users.FirstOrDefaultAsync(u => u.PhoneNumber == phoneNumber);
        return null;
    }

    private static void ValidateResetCode(ApplicationUser user, string code)
    {
        if (string.IsNullOrEmpty(user.PasswordResetCode)
            || user.PasswordResetCodeExpiry is null
            || user.PasswordResetCodeExpiry < DateTime.UtcNow)
        {
            throw new InvalidOperationException(InvalidOrExpiredCode);
        }

        if (!string.Equals(user.PasswordResetCode, HashResetCode(code), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(InvalidOrExpiredCode);
        }
    }

    private static string HashResetCode(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string GenerateJwt(ApplicationUser user)
    {
        var key = _jwtSettings.Key;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Jwt:Key is not configured. Ensure it is set in environment variables or appsettings.");

        var issuer = _jwtSettings.Issuer;
        var audience = _jwtSettings.Audience;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new(ClaimTypes.Role, user.Role ?? "User"),
            new("tier", (user.Tier ?? "free").ToLowerInvariant()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var creds = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_jwtSettings.ExpirationMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<string?> GenerateFreshTokenAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return null;
        return GenerateJwt(user);
    }

    public async Task<AuthResponse> GoogleLoginAsync(GoogleLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(_googleSettings.ClientId))
        {
            _logger.LogError("Google login attempted but Google__ClientId is not configured");
            throw new InvalidOperationException("Google login is not configured on this server.");
        }

        _logger.LogInformation("Validating Google token with ClientId length={Length}", _googleSettings.ClientId.Length);

        var payload = await GoogleJsonWebSignature.ValidateAsync(
            request.IdToken,
            new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _googleSettings.ClientId }
            });

        if (payload.EmailVerified is false)
            throw new UnauthorizedAccessException("Email not verified");

        var email = payload.Email.ToLowerInvariant();
        var name = payload.Name ?? email.Split('@')[0];

        _logger.LogInformation("Google login attempt for {Email}", email);

        var user = await _users.FindByEmailAsync(email);

        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                DisplayName = name,
                EmailConfirmed = true,
                Tier = "free",
                Role = "User",
                CreatorTier = CreatorTier.Free,
                GoogleId = payload.Subject,
                AuthProvider = "Google"
            };

            var result = await _users.CreateAsync(user);
            if (!result.Succeeded)
            {
                var errors = string.Join("; ", result.Errors.Select(e => e.Description));
                _logger.LogWarning("Google registration failed: {Email} — {Errors}", email, errors);
                throw new InvalidOperationException($"Registration failed: {errors}");
            }

            _logger.LogInformation("Google registration success: User={UserId} Email={Email}", user.Id, user.Email);
        }
        else
        {
            // Link Google identity if not already linked
            if (string.IsNullOrEmpty(user.GoogleId))
            {
                user.GoogleId = payload.Subject;
                user.AuthProvider ??= "Google";
                await _users.UpdateAsync(user);
                _logger.LogInformation("Google identity linked: User={UserId}", user.Id);
            }
        }

        var sub = await _subscriptions.GetActiveAsync(user.Id);
        var resolvedTier = sub?.Plan ?? (user.CreatorTier == CreatorTier.Pro ? "pro" : "free");

        var token = GenerateJwt(user);

        var needsUsername = string.IsNullOrWhiteSpace(user.UserName)
                            || string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase);
        var isCreatorOrAdmin = string.Equals(user.Role, "Creator", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase);
        var isNewGoogleUser = needsUsername && !isCreatorOrAdmin;

        _logger.LogInformation("Google login success: User={UserId} Tier={Tier} IsNew={IsNew}", user.Id, resolvedTier, isNewGoogleUser);

        return new AuthResponse
        {
            UserId = Guid.Parse(user.Id),
            Email = user.Email ?? "",
            Token = token,
            Tier = resolvedTier.ToLowerInvariant(),
            Role = user.Role ?? "User",
            Username = needsUsername ? null : user.UserName,
            PhoneNumber = user.PhoneNumber,
            IsNewUser = isNewGoogleUser
        };
    }

    public string GetGoogleClientId() => _googleSettings.ClientId;
}