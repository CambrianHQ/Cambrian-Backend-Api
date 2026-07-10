using Cambrian.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Stripe;
using Stripe.Checkout;

namespace Cambrian.Infrastructure.Stripe;

public class StripeFacade : IPaymentGateway
{
    private readonly string _frontendUrl;

    public StripeFacade(IConfiguration configuration)
    {
        _frontendUrl = configuration["App:FrontendUrl"]
            ?? throw new InvalidOperationException("App:FrontendUrl must be configured. Stripe checkout redirects require a valid frontend URL.");
    }

    public async Task<string> CreateCheckoutSessionAsync(
        int amountInCents,
        string productName,
        string? clientReferenceId = null,
        string? successUrl = null,
        string? cancelUrl = null,
        string? customerEmail = null)
    {
        var options = new SessionCreateOptions
        {
            Mode = "payment",
            Customer = customerEmail != null ? await FindOrCreateCustomerAsync(customerEmail) : null,
            SuccessUrl = successUrl ?? $"{_frontendUrl}/checkout/success?session_id={{CHECKOUT_SESSION_ID}}",
            CancelUrl = cancelUrl ?? $"{_frontendUrl}/checkout/cancel",
            ClientReferenceId = clientReferenceId,
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = amountInCents,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = productName
                        }
                    },
                    Quantity = 1
                }
            }
        };

        var service = new SessionService();
        var session = await service.CreateAsync(options);

        return session.Url!;
    }

    public async Task<string> CreateSubscriptionCheckoutAsync(
        int amountInCents,
        string planName,
        string clientReferenceId,
        string successUrl,
        string cancelUrl,
        string? customerEmail = null)
    {
        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customerEmail != null ? await FindOrCreateCustomerAsync(customerEmail) : null,
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            ClientReferenceId = clientReferenceId,
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = amountInCents,
                        Recurring = new SessionLineItemPriceDataRecurringOptions
                        {
                            Interval = "month"
                        },
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = $"Cambrian {planName} Plan"
                        }
                    },
                    Quantity = 1
                }
            }
        };

        var service = new SessionService();
        var session = await service.CreateAsync(options);

        return session.Url!;
    }

    public async Task<string> CreateSubscriptionCheckoutByPriceAsync(
        string priceId,
        string clientReferenceId,
        string successUrl,
        string cancelUrl,
        string? customerEmail = null,
        string? customerId = null,
        int? trialPeriodDays = null)
    {
        if (string.IsNullOrWhiteSpace(priceId))
            throw new InvalidOperationException("A Stripe Price ID is required for subscription checkout.");

        var hasTrial = trialPeriodDays is > 0;
        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            Customer = !string.IsNullOrWhiteSpace(customerId)
                ? customerId
                : customerEmail != null ? await FindOrCreateCustomerAsync(customerEmail) : null,
            PaymentMethodCollection = hasTrial ? "always" : null,
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            ClientReferenceId = clientReferenceId,
            LineItems = new List<SessionLineItemOptions>
            {
                new() { Price = priceId, Quantity = 1 }
            },
            SubscriptionData = hasTrial
                ? new SessionSubscriptionDataOptions
                {
                    TrialPeriodDays = trialPeriodDays,
                    TrialSettings = new SessionSubscriptionDataTrialSettingsOptions
                    {
                        EndBehavior = new SessionSubscriptionDataTrialSettingsEndBehaviorOptions
                        {
                            MissingPaymentMethod = "cancel"
                        }
                    }
                }
                : null
        };

        var service = new SessionService();
        var session = await service.CreateAsync(options);
        return session.Url!;
    }

    public Task<string> EnsureCustomerAsync(string email) => FindOrCreateCustomerAsync(email);

    public async Task<DateTime?> CancelSubscriptionAtPeriodEndAsync(string stripeSubscriptionId)
    {
        if (string.IsNullOrWhiteSpace(stripeSubscriptionId))
            throw new InvalidOperationException("A Stripe subscription ID is required to schedule cancellation.");

        var service = new SubscriptionService();
        // Schedule cancellation instead of deleting now — the subscriber keeps
        // access (and we keep collecting the final period they already paid for)
        // until the period ends, when Stripe emits customer.subscription.deleted.
        var updated = await service.UpdateAsync(stripeSubscriptionId, new SubscriptionUpdateOptions
        {
            CancelAtPeriodEnd = true,
        });

        return updated.CurrentPeriodEnd == default ? null : updated.CurrentPeriodEnd;
    }

    public async Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new InvalidOperationException("A Stripe customer ID is required to open the billing portal.");

        var service = new global::Stripe.BillingPortal.SessionService();
        var session = await service.CreateAsync(new global::Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = customerId,
            ReturnUrl = returnUrl
        });
        return session.Url;
    }

    public async Task<Session> GetSessionAsync(string sessionId)
    {
        var service = new SessionService();
        return await service.GetAsync(sessionId);
    }

    public async Task<CheckoutSessionInfo?> GetCheckoutSessionAsync(string sessionId)
    {
        try
        {
            var service = new SessionService();
            var session = await service.GetAsync(sessionId);

            var status = session.PaymentStatus switch
            {
                "paid" => "paid",
                "unpaid" => "pending",
                _ => "pending"
            };

            return new CheckoutSessionInfo
            {
                SessionId = session.Id,
                Status = status,
                ClientReferenceId = session.ClientReferenceId,
                AmountTotal = session.AmountTotal
            };
        }
        catch
        {
            return null;
        }
    }

    // ── Helpers ──

    /// <summary>
    /// Find an existing Stripe Customer by email, or create one if none exists.
    /// Required by Stripe Accounts V2 which does not support checkout without a customer.
    /// </summary>
    private static async Task<string> FindOrCreateCustomerAsync(string email)
    {
        var customerService = new CustomerService();
        var existing = await customerService.ListAsync(new CustomerListOptions
        {
            Email = email,
            Limit = 1
        });

        if (existing.Data.Count > 0)
            return existing.Data[0].Id;

        var customer = await customerService.CreateAsync(new CustomerCreateOptions
        {
            Email = email
        });
        return customer.Id;
    }

    // ── Stripe Connect ──

    public async Task<string> CreateConnectAccountAsync(string email)
    {
        var service = new AccountService();
        var account = await service.CreateAsync(new AccountCreateOptions
        {
            Type = "express",
            Email = email,
            Capabilities = new AccountCapabilitiesOptions
            {
                CardPayments = new AccountCapabilitiesCardPaymentsOptions { Requested = true },
                Transfers = new AccountCapabilitiesTransfersOptions { Requested = true }
            }
        });
        return account.Id;
    }

    public async Task<string> CreateAccountOnboardingLinkAsync(
        string accountId, string returnUrl, string refreshUrl)
    {
        var service = new AccountLinkService();
        var link = await service.CreateAsync(new AccountLinkCreateOptions
        {
            Account = accountId,
            Type = "account_onboarding",
            ReturnUrl = returnUrl,
            RefreshUrl = refreshUrl
        });
        return link.Url;
    }

    public async Task<ConnectAccountStatus> GetConnectAccountStatusAsync(string accountId)
    {
        var service = new AccountService();
        var account = await service.GetAsync(accountId);
        var status = (account.ChargesEnabled && account.PayoutsEnabled) ? "active" : "pending";
        return new ConnectAccountStatus
        {
            AccountId = account.Id,
            Status = status,
            ChargesEnabled = account.ChargesEnabled,
            PayoutsEnabled = account.PayoutsEnabled
        };
    }

    public async Task<string> CreateExpressDashboardLinkAsync(string accountId)
    {
        var service = new AccountLoginLinkService();
        var link = await service.CreateAsync(accountId);
        return link.Url;
    }

    public async Task<string> CreateTransferAsync(
        string destinationAccountId, long amountCents, string description, string idempotencyKey)
    {
        var service = new TransferService();
        var transfer = await service.CreateAsync(new TransferCreateOptions
        {
            Amount = amountCents,
            Currency = "usd",
            Destination = destinationAccountId,
            Description = description
        }, new RequestOptions { IdempotencyKey = idempotencyKey });
        return transfer.Id;
    }

    public async Task DeleteConnectedAccountAsync(string accountId)
    {
        var service = new AccountService();
        await service.DeleteAsync(accountId);
    }

    // ── Connect money-in: direct charges on the artist's connected account ──

    public async Task<string> CreateConnectedCheckoutAsync(
        string connectedAccountId,
        int amountInCents,
        string productName,
        string clientReferenceId,
        string successUrl,
        string cancelUrl,
        long applicationFeeCents)
    {
        var options = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            ClientReferenceId = clientReferenceId,
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = amountInCents,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = productName
                        }
                    },
                    Quantity = 1
                }
            },
            // 0 at launch for tips: omit the field entirely rather than sending 0.
            PaymentIntentData = applicationFeeCents > 0
                ? new SessionPaymentIntentDataOptions { ApplicationFeeAmount = applicationFeeCents }
                : null,
        };

        var service = new SessionService();
        var session = await service.CreateAsync(options, ConnectedAccount(connectedAccountId));
        return session.Url!;
    }

    public async Task<string> CreateConnectedSubscriptionCheckoutAsync(
        string connectedAccountId,
        int amountInCents,
        string productName,
        string clientReferenceId,
        string successUrl,
        string cancelUrl,
        decimal applicationFeePercent)
    {
        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            ClientReferenceId = clientReferenceId,
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = amountInCents,
                        Recurring = new SessionLineItemPriceDataRecurringOptions
                        {
                            Interval = "month"
                        },
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = productName
                        }
                    },
                    Quantity = 1
                }
            },
            SubscriptionData = applicationFeePercent > 0
                ? new SessionSubscriptionDataOptions { ApplicationFeePercent = applicationFeePercent }
                : null,
        };

        var service = new SessionService();
        var session = await service.CreateAsync(options, ConnectedAccount(connectedAccountId));
        return session.Url!;
    }

    /// <summary>Scope an API call to the connected account (direct charge semantics).</summary>
    private static RequestOptions ConnectedAccount(string accountId) =>
        new() { StripeAccount = accountId };
}
