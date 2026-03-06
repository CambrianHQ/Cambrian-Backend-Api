using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stripe;
using Stripe.Checkout;

namespace Cambrian.Infrastructure.Stripe;

public class StripeWebhookService : IWebhookService
{
    private readonly CambrianDbContext _db;
    private readonly string _webhookSecret;

    public StripeWebhookService(CambrianDbContext db, IConfiguration configuration)
    {
        _db = db;
        _webhookSecret = configuration["Stripe:WebhookSecret"] ?? "";
    }

    public async Task HandleStripeAsync(string payload)
    {
        // In production, verify signature with _webhookSecret:
        // var stripeEvent = EventUtility.ConstructEvent(payload, signature, _webhookSecret);

        var stripeEvent = EventUtility.ParseEvent(payload);

        switch (stripeEvent.Type)
        {
            case EventTypes.CheckoutSessionCompleted:
                await HandleCheckoutCompleted(stripeEvent);
                break;
        }
    }

    private async Task HandleCheckoutCompleted(Event stripeEvent)
    {
        var session = stripeEvent.Data.Object as Session;

        if (session?.ClientReferenceId is null)
            return;

        // Mark purchase as completed based on client reference ID
        if (Guid.TryParse(session.ClientReferenceId, out var purchaseId))
        {
            var purchase = await _db.Purchases.FindAsync(purchaseId);

            if (purchase is not null)
            {
                purchase.Status = "completed";
                await _db.SaveChangesAsync();
            }
        }
    }
}
