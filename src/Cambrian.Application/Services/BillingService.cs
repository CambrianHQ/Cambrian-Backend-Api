using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public sealed class BillingService : IBillingService
{
    private const int CreatorTrialDays = 14;

    private readonly ISubscriptionRepository _subscriptions;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IPaymentGateway _gateway;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BillingService> _logger;
    private readonly string _frontendUrl;

    public BillingService(
        ISubscriptionRepository subscriptions,
        ISubscriptionService subscriptionService,
        IPaymentGateway gateway,
        UserManager<ApplicationUser> users,
        IConfiguration configuration,
        ILogger<BillingService> logger)
    {
        _subscriptions = subscriptions;
        _subscriptionService = subscriptionService;
        _gateway = gateway;
        _users = users;
        _configuration = configuration;
        _logger = logger;
        _frontendUrl = ResolveFrontendUrl(configuration);
    }

    public async Task<CheckoutResponse> CreateCheckoutAsync(BillingCheckoutRequest request, string userId, string? userEmail = null)
    {
        var tier = (request.Tier ?? "").Trim().ToLowerInvariant();

        var successUrl = $"{_frontendUrl}/payment?payment_success=true&session_id={{CHECKOUT_SESSION_ID}}";
        var cancelUrl = $"{_frontendUrl}/payment";

        // Subscription tiers (creator/pro) use pre-created Stripe Price IDs.
        if (tier is "creator" or "pro")
        {
            var tierConfig = TierManifest.For(tier);
            var priceId = ResolvePriceId(tierConfig);
            string? stripeCustomerId = null;
            if (!string.IsNullOrWhiteSpace(userEmail))
            {
                stripeCustomerId = await _gateway.EnsureCustomerAsync(userEmail);
            }

            var hasPriorSubscription = await _subscriptions.HasAnyForUserOrCustomerAsync(userId, stripeCustomerId);
            int? trialPeriodDays = hasPriorSubscription ? null : CreatorTrialDays;

            var subUrl = await _gateway.CreateSubscriptionCheckoutByPriceAsync(
                priceId,
                clientReferenceId: $"{userId}:subscription:{tier}",
                successUrl,
                cancelUrl,
                customerEmail: stripeCustomerId is null ? userEmail : null,
                customerId: stripeCustomerId,
                trialPeriodDays: trialPeriodDays);

            return new CheckoutResponse { CheckoutUrl = subUrl };
        }

        // Legacy buyer subscription ("paid") — retained for back-compat with the existing
        // /billing flow; not part of the creator tier matrix.
        if (tier == "paid")
        {
            var url = await _gateway.CreateSubscriptionCheckoutAsync(
                amountInCents: 999,
                planName: "Buyer Subscription",
                clientReferenceId: $"{userId}:subscription:paid",
                successUrl,
                cancelUrl,
                customerEmail: userEmail);

            return new CheckoutResponse { CheckoutUrl = url };
        }

        throw new ArgumentException("Invalid tier. Choose 'creator' or 'pro'.");
    }

    public async Task<PortalResponse> CreatePortalAsync(string userId, string? userEmail = null)
    {
        // Prefer the Stripe customer id captured on the active subscription; otherwise
        // resolve one from the user's email (find-or-create).
        var sub = await _subscriptions.GetActiveAsync(userId);
        var customerId = sub?.StripeCustomerId;

        if (string.IsNullOrWhiteSpace(customerId))
        {
            var email = userEmail ?? (await _users.FindByIdAsync(userId))?.Email;
            if (string.IsNullOrWhiteSpace(email))
                throw new ArgumentException("Cannot open billing portal: no Stripe customer and no email on file.");
            customerId = await _gateway.EnsureCustomerAsync(email);
        }

        var returnUrl = $"{_frontendUrl}/settings/billing";
        var portalUrl = await _gateway.CreateBillingPortalSessionAsync(customerId!, returnUrl);

        _logger.LogInformation("EVENT: BillingPortalCreated userId:{UserId}", userId);
        return new PortalResponse { PortalUrl = portalUrl };
    }

    /// <summary>
    /// Resolve the configured Stripe Price ID for a tier. Price IDs are environment config
    /// (never hardcoded). Throws a clear error if the tier is misconfigured.
    /// </summary>
    private string ResolvePriceId(TierConfig tierConfig)
    {
        if (tierConfig.StripePriceConfigKey is null)
            throw new ArgumentException($"Tier '{tierConfig.Slug}' has no subscription price.");

        var priceId = _configuration[tierConfig.StripePriceConfigKey];
        if (string.IsNullOrWhiteSpace(priceId))
            throw new InvalidOperationException(
                $"Stripe price for the {tierConfig.DisplayName} tier is not configured ({tierConfig.StripePriceConfigKey}).");

        return priceId;
    }

    public async Task<BillingStatusResponse> GetStatusAsync(string userId)
    {
        var sub = await _subscriptions.GetActiveAsync(userId);
        var user = await _users.FindByIdAsync(userId);
        var tierConfig = user is not null
            ? TierManifest.For(user.CreatorTier)
            : TierManifest.Free;

        // F8 — the plan label must agree with the entitlement. Prefer the live
        // subscription plan; when there's no subscription record (comped/seeded
        // Pro) fall back to the authoritative creator-tier entitlement, never
        // the raw JWT tier claim, and never a hard-coded "free". This keeps
        // /settings/billing from disagreeing with the issued tier claim.
        var isPro = tierConfig.Tier == CreatorTier.Pro;
        var planLabel = !string.IsNullOrWhiteSpace(sub?.Plan)
            ? sub!.Plan
            : (isPro ? "pro" : "free");

        return new BillingStatusResponse
        {
            Tier = planLabel,
            Status = sub?.Status ?? "active",
            ExpiresAt = sub?.ExpiresAt,
            TrialEndsAt = sub?.TrialEndsAt,
            CreatorTier = tierConfig.Tier.ToString(),
            UploadCount = user?.UploadCount ?? 0,
            UploadLimit = tierConfig.UploadLimit,
            PlatformFeePercent = tierConfig.FeeRate
        };
    }

    public async Task<CheckoutSessionStatusResponse> ConfirmCheckoutAsync(string sessionId, string userId)
    {
        // Retrieve the checkout session from Stripe
        var session = await _gateway.GetCheckoutSessionAsync(sessionId);

        if (session is null)
        {
            _logger.LogWarning("Checkout session {SessionId} not found at Stripe", sessionId);
            return new CheckoutSessionStatusResponse
            {
                SessionId = sessionId,
                Status = "failed"
            };
        }

        if (session.Status != "paid")
        {
            _logger.LogInformation("Checkout session {SessionId} status is {Status}", sessionId, session.Status);
            return new CheckoutSessionStatusResponse
            {
                SessionId = sessionId,
                Status = session.Status
            };
        }

        // Parse clientReferenceId = "userId:subscription:tier"
        var parts = session.ClientReferenceId?.Split(':');
        if (parts is not { Length: 3 } || parts[1] != "subscription")
        {
            _logger.LogWarning("Checkout session {SessionId} has unexpected clientReferenceId: {Ref}",
                sessionId, session.ClientReferenceId);
            return new CheckoutSessionStatusResponse
            {
                SessionId = sessionId,
                Status = "paid"
            };
        }

        var tier = parts[2];

        // Verify the session belongs to this user
        if (parts[0] != userId)
        {
            _logger.LogWarning("Checkout session {SessionId} userId mismatch: session={SessionUser} caller={Caller}",
                sessionId, parts[0], userId);
            return new CheckoutSessionStatusResponse
            {
                SessionId = sessionId,
                Status = "failed"
            };
        }

        // Activate the subscription and update user tier using the existing SubscriptionService logic
        await _subscriptionService.UpdateAsync(new UpdateSubscriptionRequest { Plan = tier }, userId);

        _logger.LogInformation("Checkout confirmed: User={UserId} Tier={Tier} Session={SessionId}",
            userId, tier, sessionId);

        return new CheckoutSessionStatusResponse
        {
            SessionId = sessionId,
            Status = "paid",
            Tier = tier
        };
    }

    private static string ResolveFrontendUrl(IConfiguration configuration)
    {
        var configuredUrl = configuration["App:FrontendUrl"];
        if (string.IsNullOrWhiteSpace(configuredUrl))
            throw new InvalidOperationException("App:FrontendUrl must be configured. Billing checkout redirects require a valid frontend URL.");
        return configuredUrl.TrimEnd('/');
    }
}
