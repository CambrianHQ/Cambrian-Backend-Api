using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class SubscriptionServiceTests
{
    private readonly ISubscriptionRepository _subscriptions = Substitute.For<ISubscriptionRepository>();
    private readonly UserManager<ApplicationUser> _users;
    private readonly SubscriptionService _sut;

    public SubscriptionServiceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _users = Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);
        _sut = new SubscriptionService(_subscriptions, _users);
    }

    [Theory]
    [InlineData("paid")]
    [InlineData("creator")]
    public async Task UpdateAsync_Throws_WhenPaidPlanRequestedOutsideBilling(string plan)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.UpdateAsync(new UpdateSubscriptionRequest { Plan = plan }, "user-1"));
    }

    [Fact]
    public async Task UpdateAsync_DowngradesExistingSubscriptionToFree()
    {
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Plan = "paid",
            Status = "active"
        };
        var user = new ApplicationUser { Id = "user-1", Tier = "paid" };

        _subscriptions.GetActiveAsync("user-1").Returns(subscription);
        _users.FindByIdAsync("user-1").Returns(user);
        _users.UpdateAsync(user).Returns(IdentityResult.Success);

        var result = await _sut.UpdateAsync(new UpdateSubscriptionRequest { Plan = "free" }, "user-1");

        Assert.Equal("free", result.Plan);
        Assert.Equal("free", user.Tier);
        await _subscriptions.Received(1).CancelAsync(subscription.Id);
        await _users.Received(1).UpdateAsync(user);
        await _subscriptions.DidNotReceive().CreateAsync(Arg.Any<Subscription>());
    }
}
