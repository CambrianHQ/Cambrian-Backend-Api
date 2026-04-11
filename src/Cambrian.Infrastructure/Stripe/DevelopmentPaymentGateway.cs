using System.Collections.Concurrent;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Cambrian.Infrastructure.Stripe;

/// <summary>
/// Development-only payment gateway used when Stripe keys are absent locally.
/// It creates synthetic paid sessions so checkout/confirm flows can be exercised
/// without real Stripe credentials.
/// </summary>
public sealed class DevelopmentPaymentGateway : IPaymentGateway
{
    private readonly string _frontendUrl;
    private readonly ConcurrentDictionary<string, CheckoutSessionInfo> _sessions = new();

    public DevelopmentPaymentGateway(IConfiguration configuration)
    {
        _frontendUrl = configuration["App:FrontendUrl"]
            ?? throw new InvalidOperationException("App:FrontendUrl must be configured for development checkout redirects.");
    }

    public Task<string> CreateCheckoutSessionAsync(
        int amountInCents,
        string productName,
        string? clientReferenceId = null,
        string? successUrl = null,
        string? cancelUrl = null,
        string? customerEmail = null)
    {
        var sessionId = $"cs_dev_{Guid.NewGuid():N}";
        _sessions[sessionId] = new CheckoutSessionInfo
        {
            SessionId = sessionId,
            Status = "paid",
            ClientReferenceId = clientReferenceId,
            AmountTotal = amountInCents
        };

        return Task.FromResult(ResolveRedirectUrl(
            successUrl ?? $"{_frontendUrl}/checkout/success?session_id={{CHECKOUT_SESSION_ID}}",
            sessionId));
    }

    public Task<string> CreateSubscriptionCheckoutAsync(
        int amountInCents,
        string planName,
        string clientReferenceId,
        string successUrl,
        string cancelUrl,
        string? customerEmail = null)
    {
        var sessionId = $"cs_dev_sub_{Guid.NewGuid():N}";
        _sessions[sessionId] = new CheckoutSessionInfo
        {
            SessionId = sessionId,
            Status = "paid",
            ClientReferenceId = clientReferenceId,
            AmountTotal = amountInCents
        };

        return Task.FromResult(ResolveRedirectUrl(successUrl, sessionId));
    }

    public Task<CheckoutSessionInfo?> GetCheckoutSessionAsync(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task<string> CreateConnectAccountAsync(string email)
        => Task.FromResult($"acct_dev_{Guid.NewGuid():N}");

    public Task<string> CreateAccountOnboardingLinkAsync(string accountId, string returnUrl, string refreshUrl)
        => Task.FromResult(returnUrl);

    public Task<ConnectAccountStatus> GetConnectAccountStatusAsync(string accountId)
        => Task.FromResult(new ConnectAccountStatus
        {
            AccountId = accountId,
            Status = "active",
            ChargesEnabled = true,
            PayoutsEnabled = true
        });

    public Task<string> CreateExpressDashboardLinkAsync(string accountId)
        => Task.FromResult($"{_frontendUrl}/settings/payouts?account={Uri.EscapeDataString(accountId)}");

    public Task<string> CreateTransferAsync(string destinationAccountId, long amountCents, string description)
        => Task.FromResult($"tr_dev_{Guid.NewGuid():N}");

    public Task DeleteConnectedAccountAsync(string accountId)
        => Task.CompletedTask;

    private static string ResolveRedirectUrl(string template, string sessionId)
        => template.Replace("{CHECKOUT_SESSION_ID}", Uri.EscapeDataString(sessionId), StringComparison.Ordinal);
}
