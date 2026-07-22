using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.DTOs.Creators;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Unit tests for IUsernameOnboardingService — the single shared business logic behind
/// both POST /auth/set-username (self-service) and POST /admin/users/{id}/set-username
/// (admin repair for accounts stuck by a client/server onboarding-state desync).
///
/// F19 atomicity coverage (rollback-on-failure, no-role-change-for-listeners,
/// provision-for-creators) was moved here from AuthControllerTests when the inline
/// controller logic was extracted into this service — do not delete without replacing.
/// </summary>
public sealed class UsernameOnboardingServiceTests
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ICreatorIdentityRepository _creators = Substitute.For<ICreatorIdentityRepository>();
    private readonly ICreatorProfileRepository _profiles = Substitute.For<ICreatorProfileRepository>();
    private readonly ITransactionManager _tx = Substitute.For<ITransactionManager>();
    private readonly UsernameOnboardingService _service;

    public UsernameOnboardingServiceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        _tx.BeginTransactionAsync().Returns(Substitute.For<IAsyncDisposable>());
        var logger = Substitute.For<ILogger<UsernameOnboardingService>>();
        _service = new UsernameOnboardingService(_users, _creators, _profiles, _tx, logger);
    }

    [Fact]
    public async Task CompleteAsync_RollsBack_WhenCreatorUpsertFails()
    {
        var userId = "user-f19";
        var user = new ApplicationUser { Id = userId, Email = "f19@test.com", Role = "Creator" };
        _users.FindByIdAsync(userId).Returns(user);
        _users.FindByNameAsync("testcreator").Returns((ApplicationUser?)null);
        _users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);
        _creators.IsUsernameTakenAsync("testcreator", Arg.Any<Guid?>()).Returns(false);
        _creators.UpsertAsync(Arg.Any<string>(), Arg.Any<UpdateCreatorProfileRequest>())
            .ThrowsAsync(new InvalidOperationException("DB timeout"));

        var result = await _service.CompleteAsync(userId, "testcreator");

        Assert.False(result.Success);
        Assert.Equal("unexpected_error", result.FailureCode);
        await _tx.Received(1).RollbackAsync();
        await _tx.DidNotReceive().CommitAsync();
    }

    [Fact]
    public async Task CompleteAsync_CommitsTransaction_OnSuccess_AndDoesNotPromoteListener()
    {
        var userId = "user-ok";
        var user = new ApplicationUser { Id = userId, Email = "ok@test.com", Role = "User" };
        _users.FindByIdAsync(userId).Returns(user);
        _users.FindByNameAsync("goodname").Returns((ApplicationUser?)null);
        _users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);
        _creators.IsUsernameTakenAsync("goodname", Arg.Any<Guid?>()).Returns(false);

        var result = await _service.CompleteAsync(userId, "goodname");

        Assert.True(result.Success);
        Assert.Equal("goodname", result.Username);
        await _tx.Received(1).CommitAsync();
        await _tx.DidNotReceive().RollbackAsync();

        // The bug this regresses: setting a username silently promoted listeners to Creator
        // and gave them a public storefront. A listener must stay a listener.
        Assert.Equal("User", user.Role);
        await _creators.DidNotReceive().UpsertAsync(Arg.Any<string>(), Arg.Any<UpdateCreatorProfileRequest>());
    }

    [Fact]
    public async Task CompleteAsync_DoesNotChangeRole_ButProvisions_ForCreatorAccount()
    {
        var userId = "user-creator";
        var user = new ApplicationUser { Id = userId, Email = "creator@test.com", Role = "Creator" };
        _users.FindByIdAsync(userId).Returns(user);
        _users.FindByNameAsync("creatorname").Returns((ApplicationUser?)null);
        _users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);
        _creators.IsUsernameTakenAsync("creatorname", Arg.Any<Guid?>()).Returns(false);
        _creators.UpsertAsync(userId, Arg.Any<UpdateCreatorProfileRequest>())
            .Returns(new PublicCreatorDto { Id = Guid.NewGuid().ToString(), Username = "creatorname" });
        _profiles.GetByUserIdAsync(userId).Returns((CreatorProfileDto?)null);

        var result = await _service.CompleteAsync(userId, "creatorname");

        Assert.True(result.Success);
        await _tx.Received(1).CommitAsync();
        Assert.Equal("Creator", user.Role);
        await _creators.Received(1).UpsertAsync(userId, Arg.Any<UpdateCreatorProfileRequest>());
        await _profiles.Received(1).UpsertAsync(userId, "creatorname", "", null, null, false, true,
            null, null, null, null, null);
    }

    [Fact]
    public async Task CompleteAsync_Fails_WhenUsernameEmpty()
    {
        var result = await _service.CompleteAsync("any-user", "   ");
        Assert.False(result.Success);
        Assert.Equal("invalid_username", result.FailureCode);
    }

    [Theory]
    [InlineData("ab")] // too short
    [InlineData("this-username-is-way-too-long-to-be-accepted-by-the-system")] // too long
    public async Task CompleteAsync_Fails_OnInvalidLength(string username)
    {
        var result = await _service.CompleteAsync("any-user", username);
        Assert.False(result.Success);
        Assert.Equal("invalid_username", result.FailureCode);
    }

    [Fact]
    public async Task CompleteAsync_Fails_OnInvalidCharacters()
    {
        var result = await _service.CompleteAsync("any-user", "not valid!");
        Assert.False(result.Success);
        Assert.Equal("invalid_username", result.FailureCode);
    }

    [Fact]
    public async Task CompleteAsync_Fails_OnReservedUsername()
    {
        var result = await _service.CompleteAsync("any-user", "admin");
        Assert.False(result.Success);
        Assert.Equal("reserved_username", result.FailureCode);
    }

    [Fact]
    public async Task CompleteAsync_Fails_WhenUserNotFound()
    {
        _users.FindByIdAsync("missing-user").Returns((ApplicationUser?)null);
        var result = await _service.CompleteAsync("missing-user", "somename");
        Assert.False(result.Success);
        Assert.Equal("user_not_found", result.FailureCode);
    }

    [Fact]
    public async Task CompleteAsync_Fails_WhenUsernameAlreadySet()
    {
        var userId = "user-already-set";
        var user = new ApplicationUser { Id = userId, Email = "set@test.com", UserName = "existingname", Role = "Creator" };
        _users.FindByIdAsync(userId).Returns(user);

        var result = await _service.CompleteAsync(userId, "newname");

        Assert.False(result.Success);
        Assert.Equal("already_set", result.FailureCode);
        await _tx.DidNotReceive().BeginTransactionAsync();
    }

    [Fact]
    public async Task CompleteAsync_Fails_WhenUsernameTakenInIdentity()
    {
        var userId = "user-conflict-identity";
        var user = new ApplicationUser { Id = userId, Email = "conflict@test.com", Role = "User" };
        _users.FindByIdAsync(userId).Returns(user);
        _users.FindByNameAsync("takenname").Returns(new ApplicationUser { Id = "someone-else", UserName = "takenname" });

        var result = await _service.CompleteAsync(userId, "takenname");

        Assert.False(result.Success);
        Assert.Equal("username_taken", result.FailureCode);
        await _tx.Received(1).RollbackAsync();
    }

    [Fact]
    public async Task CompleteAsync_Fails_WhenUsernameTakenInCreatorsTable()
    {
        var userId = "user-conflict-creators";
        var user = new ApplicationUser { Id = userId, Email = "conflict2@test.com", Role = "User" };
        _users.FindByIdAsync(userId).Returns(user);
        _users.FindByNameAsync("takenname2").Returns((ApplicationUser?)null);
        _creators.IsUsernameTakenAsync("takenname2", Arg.Any<Guid?>()).Returns(true);

        var result = await _service.CompleteAsync(userId, "takenname2");

        Assert.False(result.Success);
        Assert.Equal("username_taken", result.FailureCode);
        await _tx.Received(1).RollbackAsync();
    }

    /// <summary>
    /// Regression test for the support ticket filed for ernestfederspiel13@gmail.com
    /// (requested handle "electric-evo"): a Creator-role account whose UserName still
    /// equals Email (the Identity sentinel for "never completed onboarding") and has no
    /// Creators row yet. This must succeed and provision both the Creators row and the
    /// CreatorProfile, exactly as the admin repair endpoint relies on.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_Succeeds_ForLegacyCreatorAccountStuckAtEmailSentinel()
    {
        var userId = "ernest-user-id";
        var user = new ApplicationUser
        {
            Id = userId,
            Email = "ernestfederspiel13@gmail.com",
            UserName = "ernestfederspiel13@gmail.com", // sentinel: never completed onboarding
            Role = "Creator"
        };
        _users.FindByIdAsync(userId).Returns(user);
        _users.FindByNameAsync("electric-evo").Returns((ApplicationUser?)null);
        _users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);
        _creators.IsUsernameTakenAsync("electric-evo", Arg.Any<Guid?>()).Returns(false);
        _creators.UpsertAsync(userId, Arg.Any<UpdateCreatorProfileRequest>())
            .Returns(new PublicCreatorDto { Id = Guid.NewGuid().ToString(), Username = "electric-evo" });
        _profiles.GetByUserIdAsync(userId).Returns((CreatorProfileDto?)null);

        var result = await _service.CompleteAsync(userId, "electric-evo");

        Assert.True(result.Success);
        Assert.Equal("electric-evo", result.Username);
        Assert.Equal("electric-evo", user.UserName);
        Assert.NotEqual(user.Email, user.UserName);
        await _creators.Received(1).UpsertAsync(userId, Arg.Any<UpdateCreatorProfileRequest>());
        await _profiles.Received(1).UpsertAsync(userId, "electric-evo", "", null, null, false, true,
            null, null, null, null, null);
    }
}
