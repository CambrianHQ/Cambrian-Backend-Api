using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Cambrian.Application.Services;

public sealed class AccountService : IAccountService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ISubscriptionRepository _subscriptions;

    public AccountService(UserManager<ApplicationUser> users, ISubscriptionRepository subscriptions)
    {
        _users = users;
        _subscriptions = subscriptions;
    }

    public async Task<AccountResponse> GetAccountAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null)
            throw new KeyNotFoundException("User not found.");

        var sub = await _subscriptions.GetActiveAsync(userId);

        return new AccountResponse
        {
            Id = user.Id,
            Email = user.Email,
            Plan = sub?.Plan ?? user.Tier ?? "free",
            Region = "US",
            Status = user.Status ?? "active"
        };
    }
}
