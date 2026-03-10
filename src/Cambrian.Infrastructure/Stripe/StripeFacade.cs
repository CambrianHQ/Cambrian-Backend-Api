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
        _frontendUrl = configuration["App:FrontendUrl"] ?? "http://localhost:5173";
    }

    public async Task<string> CreateCheckoutSessionAsync(
        int amountInCents,
        string productName,
        string? clientReferenceId = null,
        string? successUrl = null,
        string? cancelUrl = null)
    {
        // Stripe Accounts V2 requires a Customer for test-mode Checkout sessions
        var customerService = new CustomerService();
        var customer = await customerService.CreateAsync(new CustomerCreateOptions
        {
            Metadata = new Dictionary<string, string>
            {
                { "cambrian_ref", clientReferenceId ?? "" }
            }
        });

        var options = new SessionCreateOptions
        {
            Mode = "payment",
            Customer = customer.Id,
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
        string cancelUrl)
    {
        // Stripe Accounts V2 requires a Customer object for Checkout sessions in test mode
        var customerService = new CustomerService();
        var customer = await customerService.CreateAsync(new CustomerCreateOptions
        {
            Metadata = new Dictionary<string, string>
            {
                { "cambrian_user_id", clientReferenceId }
            }
        });

        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customer.Id,
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