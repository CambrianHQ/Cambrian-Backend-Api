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
        string destinationAccountId, long amountCents, string description)
    {
        var service = new TransferService();
        var transfer = await service.CreateAsync(new TransferCreateOptions
        {
            Amount = amountCents,
            Currency = "usd",
            Destination = destinationAccountId,
            Description = description
        });
        return transfer.Id;
    }

    public async Task DeleteConnectedAccountAsync(string accountId)
    {
        var service = new AccountService();
        await service.DeleteAsync(accountId);
    }
}