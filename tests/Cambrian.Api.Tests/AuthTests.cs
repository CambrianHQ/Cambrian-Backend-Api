using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Integration tests for the Auth flow:
///   Register → Login → /auth/me → Logout
/// Ensures tier and role are returned correctly.
/// </summary>
[Trait("Category", "Critical")]
public sealed class AuthTests
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IOptions<JwtSettings> _jwtOptions;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IEmailService _email;
    private readonly AuthService _sut;

    public AuthTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _users = Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);

        _jwtOptions = Options.Create(new JwtSettings
        {
            Key = "test-secret-key-that-is-long-enough-for-hmac256!",
            Issuer = "test-issuer",
            Audience = "test-audience"
        });

        _subscriptions = Substitute.For<ISubscriptionRepository>();
        _email = Substitute.For<IEmailService>();
        var googleOptions = Options.Create(new GoogleSettings { ClientId = "test-google-client-id" });
        var logger = Substitute.For<ILogger<AuthService>>();
        _sut = new AuthService(_users, _jwtOptions, googleOptions, _subscriptions, _email, logger);
    }

    [Fact]
    public async Task LoginAsync_ThrowsUnauthorized_WhenUserNotFound()
    {
        _users.FindByEmailAsync("nobody@test.com").Returns((ApplicationUser?)null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _sut.LoginAsync(new LoginRequest { Email = "nobody@test.com", Password = "pass" }));
    }

    [Fact]
    public async Task LoginAsync_ThrowsUnauthorized_WhenPasswordWrong()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid().ToString(), Email = "user@test.com" };
        _users.FindByEmailAsync("user@test.com").Returns(user);
        _users.CheckPasswordAsync(user, "wrong").Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _sut.LoginAsync(new LoginRequest { Email = "user@test.com", Password = "wrong" }));
    }

    [Fact]
    public async Task LoginAsync_ReturnsActualUserTier()
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "pro@test.com",
            Tier = "creator",
            Role = "User",
            CreatorTier = Domain.Enums.CreatorTier.Pro
        };
        _users.FindByEmailAsync("pro@test.com").Returns(user);
        _users.CheckPasswordAsync(user, "correct").Returns(true);

        var result = await _sut.LoginAsync(new LoginRequest { Email = "pro@test.com", Password = "correct" });

        Assert.Equal("pro", result.Tier);
        Assert.Equal("pro@test.com", result.Email);
        Assert.NotEmpty(result.Token);
    }

    [Fact]
    public async Task LoginAsync_DefaultsTierToFree_WhenNull()
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "new@test.com",
            Tier = null!,
            Role = "User"
        };
        _users.FindByEmailAsync("new@test.com").Returns(user);
        _users.CheckPasswordAsync(user, "pass").Returns(true);

        var result = await _sut.LoginAsync(new LoginRequest { Email = "new@test.com", Password = "pass" });

        Assert.Equal("free", result.Tier);
    }

    [Fact]
    public async Task LoginAsync_NormalizesTierToLowerCase()
    {
        var userId = Guid.NewGuid().ToString();
        var user = new ApplicationUser
        {
            Id = userId,
            Email = "user@test.com",
            Tier = "PAID",
            Role = "User"
        };
        _users.FindByEmailAsync("user@test.com").Returns(user);
        _users.CheckPasswordAsync(user, "pass").Returns(true);
        _subscriptions.GetActiveAsync(userId).Returns(new Subscription { Plan = "PAID" });

        var result = await _sut.LoginAsync(new LoginRequest { Email = "user@test.com", Password = "pass" });

        Assert.Equal("paid", result.Tier);
    }

    [Fact]
    public async Task RegisterAsync_ThrowsInvalidOperation_WhenRegistrationFails()
    {
        _users.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Failed(new IdentityError { Description = "Password too weak" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RegisterAsync(new RegisterRequest
            {
                Email = "new@test.com",
                Password = "weak"
            }));
    }

    [Fact]
    public async Task RegisterAsync_ReturnsFreeTier_ForNewUser()
    {
        _users.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);

        var result = await _sut.RegisterAsync(new RegisterRequest
        {
            Email = "new@test.com",
            Password = "StrongPassword123!"
        });

        Assert.Equal("free", result.Tier);
        Assert.Equal("new@test.com", result.Email);
        Assert.NotEmpty(result.Token);
    }

    [Fact]
    public async Task RegisterAsync_SetsDisplayName_WhenProvided()
    {
        _users.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);

        await _sut.RegisterAsync(new RegisterRequest
        {
            Email = "dj@test.com",
            Password = "StrongPassword123!",
            DisplayName = "DJ Cool"
        });

        await _users.Received(1).CreateAsync(
            Arg.Is<ApplicationUser>(u => u.DisplayName == "DJ Cool"),
            Arg.Any<string>());
    }

    [Fact]
    public async Task RegisterAsync_DerivesDisplayName_WhenNotProvided()
    {
        _users.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);

        await _sut.RegisterAsync(new RegisterRequest
        {
            Email = "beatmaker@music.com",
            Password = "StrongPassword123!"
        });

        await _users.Received(1).CreateAsync(
            Arg.Is<ApplicationUser>(u => u.DisplayName == "beatmaker"),
            Arg.Any<string>());
    }

    [Fact]
    public async Task LoginAsync_JwtContainsCorrectClaims()
    {
        var userId = Guid.NewGuid().ToString();
        var user = new ApplicationUser
        {
            Id = userId,
            Email = "jwt@test.com",
            Role = "Admin",
            Tier = "creator"
        };
        _users.FindByEmailAsync("jwt@test.com").Returns(user);
        _users.CheckPasswordAsync(user, "pass").Returns(true);

        var result = await _sut.LoginAsync(new LoginRequest { Email = "jwt@test.com", Password = "pass" });

        var handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-secret-key-that-is-long-enough-for-hmac256!"));
        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "test-issuer",
            ValidateAudience = true,
            ValidAudience = "test-audience",
            ValidateLifetime = true,
            IssuerSigningKey = key
        };

        var principal = handler.ValidateToken(result.Token, validationParams, out _);

        var subValue = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        Assert.Equal(userId, subValue);
        var emailValue = principal.FindFirstValue(ClaimTypes.Email)
                      ?? principal.FindFirstValue(JwtRegisteredClaimNames.Email);
        Assert.Equal("jwt@test.com", emailValue);
        Assert.Equal("Admin", principal.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task GetCurrentUserAsync_ThrowsUnauthorized_WhenNoIdentityClaim()
    {
        var emptyPrincipal = new ClaimsPrincipal(new ClaimsIdentity());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _sut.GetCurrentUserAsync(emptyPrincipal));
    }

    [Fact]
    public async Task GetCurrentUserAsync_ThrowsUnauthorized_WhenUserNotFound()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "missing-user-id")
        }));
        _users.FindByIdAsync("missing-user-id").Returns((ApplicationUser?)null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _sut.GetCurrentUserAsync(principal));
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsProfile()
    {
        var userId = Guid.NewGuid().ToString();
        var user = new ApplicationUser
        {
            Id = userId,
            Email = "profile@test.com",
            DisplayName = "Cool DJ",
            Role = "Creator",
            Tier = "paid",
            VerifiedCreator = true
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        }));
        _users.FindByIdAsync(userId).Returns(user);

        var result = await _sut.GetCurrentUserAsync(principal);

        Assert.Equal(userId, result.UserId);
        Assert.Equal("profile@test.com", result.Email);
        Assert.Equal("Cool DJ", result.DisplayName);
        Assert.Equal("Creator", result.Role);
        Assert.Equal("paid", result.Tier);
        Assert.True(result.VerifiedCreator);
    }
}
