using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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

    public AuthService(UserManager<ApplicationUser> users, IConfiguration config, ISubscriptionRepository subscriptions)
    {
        _users = users;
        _config = config;
        _subscriptions = subscriptions;
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

    public Task ForgotPasswordAsync(ForgotPasswordRequest request)
    {
        // TODO: Send password reset code via email/SMS
        return Task.CompletedTask;
    }

    public Task VerifyCodeAsync(VerifyCodeRequest request)
    {
        // TODO: Verify the code against stored reset codes
        return Task.CompletedTask;
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request)
    {
        var user = request.Email is not null
            ? await _users.FindByEmailAsync(request.Email)
            : null;

        if (user is null)
            throw new InvalidOperationException("User not found");

        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, request.NewPassword);

        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Password reset failed: {errors}");
        }
    }

    public Task RecoverUsernameAsync(RecoverUsernameRequest request)
    {
        // TODO: Send username recovery via email/SMS
        return Task.CompletedTask;
    }

    private string GenerateJwt(ApplicationUser user)
    {
        var rawKey = _config["Jwt:Key"];
        var key = string.IsNullOrWhiteSpace(rawKey) ? "cambrian-dev-secret-key-min-32-chars!!" : rawKey;
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