using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Cambrian.Application.Services;

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IConfiguration _config;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IEmailService _email;

    /// <summary>How long a password reset code is valid.</summary>
    private static readonly TimeSpan ResetCodeLifetime = TimeSpan.FromMinutes(15);

    public AuthService(
        UserManager<ApplicationUser> users,
        IConfiguration config,
        ISubscriptionRepository subscriptions,
        IEmailService email)
    {
        _users = users;
        _config = config;
        _subscriptions = subscriptions;
        _email = email;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        var user = await _users.FindByEmailAsync(request.Email);

        if (user is null)
            throw new UnauthorizedAccessException("Invalid credentials");

        var valid = await _users.CheckPasswordAsync(user, request.Password);

        if (!valid)
            throw new UnauthorizedAccessException("Invalid credentials");

        var token = GenerateJwt(user);

        return new AuthResponse
        {
            UserId = Guid.Parse(user.Id),
            Email = user.Email ?? "",
            Token = token,
            Tier = (user.Tier ?? "free").ToLowerInvariant(),
            Role = user.Role ?? "User"
        };
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
    {
        var user = new ApplicationUser
        {
            Email = request.Email,
            UserName = request.Email,
            DisplayName = request.DisplayName ?? request.Email.Split('@')[0]
        };

        var result = await _users.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Registration failed: {errors}");
        }

        var token = GenerateJwt(user);

        return new AuthResponse
        {
            UserId = Guid.Parse(user.Id),
            Email = user.Email ?? "",
            Token = token,
            Tier = "free",
            Role = "User"
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

        return new UserProfileResponse
        {
            UserId = user.Id,
            Email = user.Email ?? "",
            DisplayName = user.DisplayName,
            Role = user.Role,
            Tier = user.Tier,
            VerifiedCreator = user.VerifiedCreator
        };
    }

    public async Task<AuthResponse> GetSessionAsync(ClaimsPrincipal principal)
    {
        var profile = await GetCurrentUserAsync(principal);
        var sub = await _subscriptions.GetActiveAsync(profile.UserId);
        var tier = sub?.Plan ?? profile.Tier ?? "free";

        return new AuthResponse
        {
            UserId = Guid.Parse(profile.UserId),
            Email = profile.Email,
            Token = "",
            Tier = tier.ToLowerInvariant()
        };
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return; // silent - do not reveal whether the email exists

        var user = await _users.FindByEmailAsync(request.Email);
        if (user is null)
            return; // silent

        // Generate a cryptographically random 6-digit code
        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();

        user.PasswordResetCode = code;
        user.PasswordResetCodeExpiry = DateTime.UtcNow.Add(ResetCodeLifetime);
        await _users.UpdateAsync(user);

        await _email.SendPasswordResetAsync(user.Email!, code);
    }

    public async Task VerifyCodeAsync(VerifyCodeRequest request)
    {
        var user = await FindUserByContact(request.Email, request.PhoneNumber);
        if (user is null)
            throw new InvalidOperationException("Invalid or expired code.");

        ValidateResetCode(user, request.Code);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request)
    {
        var user = await FindUserByContact(request.Email, request.PhoneNumber);
        if (user is null)
            throw new InvalidOperationException("Invalid or expired code.");

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
        if (string.IsNullOrWhiteSpace(request.Email))
            return; // silent - do not reveal whether the email exists

        var user = await _users.FindByEmailAsync(request.Email);
        if (user is null)
            return; // silent

        // Send the username (display name or email) via email
        await _email.SendAsync(
            user.Email!,
            "Cambrian - Your Username",
            $"<p>Your username is: <strong>{user.DisplayName ?? user.Email}</strong></p>");
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

    public async Task ChangeEmailAsync(ClaimsPrincipal principal, ChangeEmailRequest request)
    {
        var user = await GetRequiredUser(principal);

        // Verify the current password before allowing email change
        var passwordValid = await _users.CheckPasswordAsync(user, request.Password);
        if (!passwordValid)
            throw new UnauthorizedAccessException("Invalid password.");

        // Check that the new email is not already taken
        var existing = await _users.FindByEmailAsync(request.NewEmail);
        if (existing is not null && existing.Id != user.Id)
            throw new InvalidOperationException("Email is already in use.");

        var token = await _users.GenerateChangeEmailTokenAsync(user, request.NewEmail);
        var result = await _users.ChangeEmailAsync(user, request.NewEmail, token);

        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Email change failed: {errors}");
        }

        // Keep UserName in sync with Email
        user.UserName = request.NewEmail;
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

    private async Task<ApplicationUser?> FindUserByContact(string? email, string? phone)
    {
        if (!string.IsNullOrWhiteSpace(email))
            return await _users.FindByEmailAsync(email);

        // Phone-based lookup not yet implemented
        return null;
    }

    private static void ValidateResetCode(ApplicationUser user, string code)
    {
        if (string.IsNullOrEmpty(user.PasswordResetCode)
            || user.PasswordResetCodeExpiry is null
            || user.PasswordResetCodeExpiry < DateTime.UtcNow)
        {
            throw new InvalidOperationException("Invalid or expired code.");
        }

        if (!string.Equals(user.PasswordResetCode, code, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid or expired code.");
        }
    }

    private string GenerateJwt(ApplicationUser user)
    {
        var key = _config["Jwt:Key"] ?? "cambrian-dev-secret-key-min-32-chars!!";
        var issuer = _config["Jwt:Issuer"] ?? "cambrian-api";
        var audience = _config["Jwt:Audience"] ?? "cambrian-client";

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new(ClaimTypes.Role, user.Role),
            new("tier", (user.Tier ?? "free").ToLowerInvariant()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var creds = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<string?> GenerateFreshTokenAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return null;
        return GenerateJwt(user);
    }
}