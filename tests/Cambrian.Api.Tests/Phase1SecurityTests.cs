using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Phase 1 tests: password reset flow, settings endpoints, upload validation.
/// </summary>
public sealed class Phase1SecurityTests
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IOptions<JwtSettings> _jwtOptions;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IEmailService _email;
    private readonly AuthService _sut;

    public Phase1SecurityTests()
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
        var sms = Substitute.For<ISmsService>();
        var googleOptions = Options.Create(new GoogleSettings { ClientId = "test-google-client-id" });
        var logger = Substitute.For<ILogger<AuthService>>();
        _sut = new AuthService(_users, _jwtOptions, googleOptions, _subscriptions, _email, sms, logger);
    }

    private static ClaimsPrincipal MakeUser(string userId = "user-1") =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }));

    // ── ForgotPassword Tests ──

    [Fact]
    public async Task ForgotPassword_StoresCodeAndSendsEmail()
    {
        var user = new ApplicationUser
        {
            Id = "u1",
            Email = "test@example.com",
            UserName = "test@example.com"
        };
        _users.FindByEmailAsync("test@example.com").Returns(user);
        _users.UpdateAsync(user).Returns(IdentityResult.Success);

        await _sut.ForgotPasswordAsync(new ForgotPasswordRequest { Email = "test@example.com" });

        // Code should be stored on the user (as SHA-256 hash → 64 hex chars)
        Assert.NotNull(user.PasswordResetCode);
        Assert.Equal(64, user.PasswordResetCode!.Length);
        Assert.NotNull(user.PasswordResetCodeExpiry);
        Assert.True(user.PasswordResetCodeExpiry > DateTime.UtcNow);

        // Email should have been sent with the plaintext code (not the hash)
        await _email.Received(1).SendPasswordResetAsync("test@example.com",
            Arg.Is<string>(c => c.Length == 8));
    }

    [Fact]
    public async Task ForgotPassword_SilentlySucceeds_WhenEmailNotFound()
    {
        _users.FindByEmailAsync("nobody@example.com").Returns((ApplicationUser?)null);

        // Should not throw — silent to prevent user enumeration
        await _sut.ForgotPasswordAsync(new ForgotPasswordRequest { Email = "nobody@example.com" });

        await _email.DidNotReceive().SendPasswordResetAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    // ── VerifyCode Tests ──

    [Fact]
    public async Task VerifyCode_Succeeds_WithValidCode()
    {
        var user = new ApplicationUser
        {
            Id = "u1",
            Email = "test@example.com",
            PasswordResetCode = HashCode("123456"),
            PasswordResetCodeExpiry = DateTime.UtcNow.AddMinutes(10)
        };
        _users.FindByEmailAsync("test@example.com").Returns(user);

        // Should not throw
        await _sut.VerifyCodeAsync(new VerifyCodeRequest { Email = "test@example.com", Code = "123456" });
    }

    [Fact]
    public async Task VerifyCode_Throws_WhenCodeIsWrong()
    {
        var user = new ApplicationUser
        {
            Id = "u1",
            Email = "test@example.com",
            PasswordResetCode = HashCode("123456"),
            PasswordResetCodeExpiry = DateTime.UtcNow.AddMinutes(10)
        };
        _users.FindByEmailAsync("test@example.com").Returns(user);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.VerifyCodeAsync(new VerifyCodeRequest { Email = "test@example.com", Code = "000000" }));

        Assert.Contains("Invalid or expired", ex.Message);
    }

    [Fact]
    public async Task VerifyCode_Throws_WhenCodeIsExpired()
    {
        var user = new ApplicationUser
        {
            Id = "u1",
            Email = "test@example.com",
            PasswordResetCode = HashCode("123456"),
            PasswordResetCodeExpiry = DateTime.UtcNow.AddMinutes(-1) // expired
        };
        _users.FindByEmailAsync("test@example.com").Returns(user);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.VerifyCodeAsync(new VerifyCodeRequest { Email = "test@example.com", Code = "123456" }));
    }

    // ── ResetPassword Tests ──

    [Fact]
    public async Task ResetPassword_RequiresValidCode()
    {
        var user = new ApplicationUser
        {
            Id = "u1",
            Email = "test@example.com",
            PasswordResetCode = null, // no code set
            PasswordResetCodeExpiry = null
        };
        _users.FindByEmailAsync("test@example.com").Returns(user);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.ResetPasswordAsync(new ResetPasswordRequest
            {
                Email = "test@example.com",
                Code = "123456",
                NewPassword = "NewStrongP@ss1"
            }));
    }

    [Fact]
    public async Task ResetPassword_Succeeds_WithValidCode_ThenInvalidatesCode()
    {
        var user = new ApplicationUser
        {
            Id = "u1",
            Email = "test@example.com",
            PasswordResetCode = HashCode("654321"),
            PasswordResetCodeExpiry = DateTime.UtcNow.AddMinutes(10)
        };
        _users.FindByEmailAsync("test@example.com").Returns(user);
        _users.GeneratePasswordResetTokenAsync(user).Returns("reset-token");
        _users.ResetPasswordAsync(user, "reset-token", "NewStrongP@ss1")
            .Returns(IdentityResult.Success);
        _users.UpdateAsync(user).Returns(IdentityResult.Success);

        await _sut.ResetPasswordAsync(new ResetPasswordRequest
        {
            Email = "test@example.com",
            Code = "654321",
            NewPassword = "NewStrongP@ss1"
        });

        // Code should be cleared
        Assert.Null(user.PasswordResetCode);
        Assert.Null(user.PasswordResetCodeExpiry);
    }

    // ── ChangePassword Tests ──

    [Fact]
    public async Task ChangePassword_CallsIdentityWithCorrectArgs()
    {
        var user = new ApplicationUser { Id = "u1", Email = "test@example.com" };
        _users.FindByIdAsync("u1").Returns(user);
        _users.ChangePasswordAsync(user, "oldpass", "NewP@ssword1!")
            .Returns(IdentityResult.Success);

        await _sut.ChangePasswordAsync(
            MakeUser("u1"),
            new ChangePasswordRequest { CurrentPassword = "oldpass", NewPassword = "NewP@ssword1!" });

        await _users.Received(1).ChangePasswordAsync(user, "oldpass", "NewP@ssword1!");
    }

    [Fact]
    public async Task ChangePassword_Throws_WhenCurrentPasswordWrong()
    {
        var user = new ApplicationUser { Id = "u1", Email = "test@example.com" };
        _users.FindByIdAsync("u1").Returns(user);
        _users.ChangePasswordAsync(user, "wrong", Arg.Any<string>())
            .Returns(IdentityResult.Failed(new IdentityError { Description = "Incorrect password" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.ChangePasswordAsync(
                MakeUser("u1"),
                new ChangePasswordRequest { CurrentPassword = "wrong", NewPassword = "NewP@ss1!" }));
    }

    // ── ChangeEmail Tests ──

    [Fact]
    public async Task ChangeEmail_Succeeds_WithValidPassword()
    {
        var user = new ApplicationUser { Id = "u1", Email = "old@example.com", UserName = "old@example.com" };
        _users.FindByIdAsync("u1").Returns(user);
        _users.CheckPasswordAsync(user, "correct").Returns(true);
        _users.FindByEmailAsync("new@example.com").Returns((ApplicationUser?)null);
        _users.GenerateChangeEmailTokenAsync(user, "new@example.com").Returns("email-token");
        _users.ChangeEmailAsync(user, "new@example.com", "email-token").Returns(IdentityResult.Success);
        _users.UpdateAsync(user).Returns(IdentityResult.Success);

        await _sut.ChangeEmailAsync(
            MakeUser("u1"),
            new ChangeEmailRequest { Password = "correct", NewEmail = "new@example.com" });

        Assert.Equal("new@example.com", user.UserName);
    }

    [Fact]
    public async Task ChangeEmail_Throws_WhenPasswordWrong()
    {
        var user = new ApplicationUser { Id = "u1", Email = "old@example.com" };
        _users.FindByIdAsync("u1").Returns(user);
        _users.CheckPasswordAsync(user, "wrong").Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _sut.ChangeEmailAsync(
                MakeUser("u1"),
                new ChangeEmailRequest { Password = "wrong", NewEmail = "new@example.com" }));
    }

    [Fact]
    public async Task ChangeEmail_Throws_WhenEmailAlreadyTaken()
    {
        var user = new ApplicationUser { Id = "u1", Email = "old@example.com" };
        var other = new ApplicationUser { Id = "u2", Email = "taken@example.com" };
        _users.FindByIdAsync("u1").Returns(user);
        _users.CheckPasswordAsync(user, "correct").Returns(true);
        _users.FindByEmailAsync("taken@example.com").Returns(other);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.ChangeEmailAsync(
                MakeUser("u1"),
                new ChangeEmailRequest { Password = "correct", NewEmail = "taken@example.com" }));
    }

    private static string HashCode(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
