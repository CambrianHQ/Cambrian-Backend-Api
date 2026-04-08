using Cambrian.Application.DTOs.Waitlist;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Unit tests for WaitlistService (issue #72).
///
/// Asserts:
///  - Email validation rejects empty / malformed values
///  - Email is normalized (trim + lowercase) before persisting
///  - Re-signup with the same email is idempotent (AlreadySignedUp = true,
///    no second AddAsync call)
/// </summary>
public sealed class WaitlistServiceTests
{
    private readonly IWaitlistRepository _repo = Substitute.For<IWaitlistRepository>();
    private readonly ILogger<WaitlistService> _logger = Substitute.For<ILogger<WaitlistService>>();
    private readonly WaitlistService _sut;

    public WaitlistServiceTests()
    {
        _sut = new WaitlistService(_repo, _logger);
    }

    [Fact]
    public async Task SignupAsync_NewEmail_PersistsAndReturnsNotAlreadySignedUp()
    {
        _repo.GetByEmailAsync("alice@example.com").Returns((WaitlistSignup?)null);

        var result = await _sut.SignupAsync(new WaitlistSignupRequest { Email = "Alice@Example.com" });

        Assert.False(result.AlreadySignedUp);
        await _repo.Received(1).AddAsync(Arg.Is<WaitlistSignup>(s =>
            s.Email == "alice@example.com" &&  // normalized
            s.Source == null));
    }

    [Fact]
    public async Task SignupAsync_DuplicateEmail_IsIdempotent()
    {
        // Existing row in the repo for the same normalized email.
        _repo.GetByEmailAsync("bob@example.com").Returns(new WaitlistSignup
        {
            Id = Guid.NewGuid(),
            Email = "bob@example.com",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        });

        var result = await _sut.SignupAsync(new WaitlistSignupRequest { Email = "  BOB@example.com  " });

        Assert.True(result.AlreadySignedUp);
        await _repo.DidNotReceive().AddAsync(Arg.Any<WaitlistSignup>());
    }

    [Fact]
    public async Task SignupAsync_PersistsSourceWhenProvided()
    {
        _repo.GetByEmailAsync(Arg.Any<string>()).Returns((WaitlistSignup?)null);

        await _sut.SignupAsync(new WaitlistSignupRequest
        {
            Email = "carol@example.com",
            Source = "homepage-hero"
        });

        await _repo.Received(1).AddAsync(Arg.Is<WaitlistSignup>(s =>
            s.Source == "homepage-hero"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]   // no @
    [InlineData("@nodomain")]      // empty local part
    [InlineData("nolocal@")]       // empty domain
    public async Task SignupAsync_InvalidEmail_Throws(string email)
    {
        // Repo lookup is bypassed for invalid input — assert the throw.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.SignupAsync(new WaitlistSignupRequest { Email = email }));

        await _repo.DidNotReceive().AddAsync(Arg.Any<WaitlistSignup>());
    }

    [Fact]
    public async Task SignupAsync_NullRequest_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.SignupAsync(null!));
    }
}
