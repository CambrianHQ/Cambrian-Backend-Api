using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class CreatorConnectService : ICreatorConnectService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IPaymentGateway _gateway;
    private readonly IConfiguration _config;
    private readonly ILogger<CreatorConnectService> _logger;

    public CreatorConnectService(
        UserManager<ApplicationUser> users,
        IPaymentGateway gateway,
        IConfiguration config,
        ILogger<CreatorConnectService> logger)
    {
        _users = users;
        _gateway = gateway;
        _config = config;
        _logger = logger;
    }

    public async Task<CreatorConnectResult> StartOnboardingAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        // Re-use existing Stripe account if one was created but onboarding wasn't completed
        var accountId = user.StripeAccountId;

        if (string.IsNullOrEmpty(accountId))
        {
            accountId = await _gateway.CreateConnectAccountAsync(user.Email!);
            user.StripeAccountId = accountId;
            await _users.UpdateAsync(user);

            _logger.LogInformation(
                "Created Stripe Connect account {AccountId} for user {UserId}",
                accountId, userId);
        }

        var frontendUrl = _config["App:FrontendUrl"]
            ?? throw new InvalidOperationException("App:FrontendUrl must be configured. Stripe Connect onboarding requires a valid frontend URL.");
        var returnUrl = $"{frontendUrl}/payouts?stripe_connect=complete";
        var refreshUrl = $"{frontendUrl}/payouts?stripe_connect=refresh";

        var onboardingUrl = await _gateway.CreateAccountOnboardingLinkAsync(
            accountId, returnUrl, refreshUrl);

        return new CreatorConnectResult
        {
            ConnectUrl = onboardingUrl,
            Status = "pending"
        };
    }

    public async Task<CreatorConnectStatusResponse> GetStatusAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        if (string.IsNullOrEmpty(user.StripeAccountId))
        {
            return new CreatorConnectStatusResponse
            {
                Connected = false,
                AccountId = null,
                Status = "not_connected"
            };
        }

        try
        {
            var status = await _gateway.GetConnectAccountStatusAsync(user.StripeAccountId);
            return new CreatorConnectStatusResponse
            {
                Connected = status.Status == "active",
                AccountId = user.StripeAccountId,
                Status = status.Status
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to retrieve Connect status for account {AccountId}",
                user.StripeAccountId);

            return new CreatorConnectStatusResponse
            {
                Connected = false,
                AccountId = user.StripeAccountId,
                Status = "pending"
            };
        }
    }

    public async Task<string?> GetDashboardLinkAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        if (string.IsNullOrEmpty(user.StripeAccountId))
            return null;

        // Verify account is active before generating dashboard link
        var status = await _gateway.GetConnectAccountStatusAsync(user.StripeAccountId);
        if (status.Status != "active")
            return null;

        return await _gateway.CreateExpressDashboardLinkAsync(user.StripeAccountId);
    }

    public async Task DisconnectAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        if (string.IsNullOrEmpty(user.StripeAccountId))
            return; // Already disconnected — idempotent

        try
        {
            await _gateway.DeleteConnectedAccountAsync(user.StripeAccountId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete Stripe account {AccountId} — clearing local reference",
                user.StripeAccountId);
        }

        var previousAccountId = user.StripeAccountId;
        user.StripeAccountId = null;
        await _users.UpdateAsync(user);

        _logger.LogInformation(
            "Disconnected Stripe account {AccountId} for user {UserId}",
            previousAccountId, userId);
    }
}
