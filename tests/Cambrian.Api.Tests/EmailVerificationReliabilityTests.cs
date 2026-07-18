using System.Security.Claims;
using Cambrian.Application.Configuration;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class EmailVerificationReliabilityTests
{
    [Fact]
    public async Task Resend_ProviderFailure_IsVisible_AndRestoresPreviouslyDeliveredToken()
    {
        var user = User(expiry: DateTime.UtcNow.AddHours(1), token: "previous-hash");
        var email = Substitute.For<IEmailService>();
        email.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns<Task>(_ => throw new HttpRequestException("provider unavailable"));
        var sut = Create(user, email);

        await Assert.ThrowsAsync<VerificationEmailDeliveryException>(() =>
            sut.SendEmailVerificationAsync(Principal(user.Id)));

        Assert.Equal("previous-hash", user.EmailVerificationToken);
        Assert.NotNull(user.EmailVerificationTokenExpiry);
    }

    [Fact]
    public async Task Resend_WithinFiveMinutes_IsRejected_WithoutDuplicateProviderCall()
    {
        var user = User(expiry: DateTime.UtcNow.AddHours(24).AddMinutes(-1), token: "recent-hash");
        var email = Substitute.For<IEmailService>();
        var sut = Create(user, email);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SendEmailVerificationAsync(Principal(user.Id)));

        Assert.Contains("wait 5 minutes", ex.Message);
        await email.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task VerifyEmail_ExpiredToken_FailsSafely_WithoutChangingVerifiedState()
    {
        var user = User(expiry: DateTime.UtcNow.AddMinutes(-1), token: "expired-hash");
        var sut = Create(user, Substitute.For<IEmailService>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.VerifyEmailAsync($"{user.Id}.expired"));

        Assert.False(user.EmailVerified);
        Assert.Equal("expired-hash", user.EmailVerificationToken);
    }

    private static AuthService Create(ApplicationUser user, IEmailService email)
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        users.FindByIdAsync(user.Id).Returns(user);
        users.UpdateAsync(user).Returns(IdentityResult.Success);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:FrontendUrl"] = "https://app.test"
        }).Build();
        return new AuthService(
            users,
            Options.Create(new JwtSettings { Key = new string('k', 64), Issuer = "test", Audience = "test" }),
            Options.Create(new GoogleSettings()),
            Substitute.For<ISubscriptionRepository>(),
            email,
            Substitute.For<ISmsService>(),
            config,
            Substitute.For<ILogger<AuthService>>());
    }

    private static ApplicationUser User(DateTime expiry, string token) => new()
    {
        Id = Guid.NewGuid().ToString(), Email = "creator@test.com", UserName = "creator@test.com",
        EmailVerified = false, EmailVerificationToken = token, EmailVerificationTokenExpiry = expiry,
    };

    private static ClaimsPrincipal Principal(string userId) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "test"));
}
