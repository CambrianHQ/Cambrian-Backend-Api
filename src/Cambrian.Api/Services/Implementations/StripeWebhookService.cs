using Cambrian.Api.Data;
using Stripe;

namespace Cambrian.Api.Services;

public class StripeWebhookService
{
    private readonly ApplicationDbContext _db;

    public StripeWebhookService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task Handle(string json, string signature)
    {
        var stripeEvent = EventUtility.ConstructEvent(
            json,
            signature,
            "YOUR_WEBHOOK_SECRET"
        );

        if (stripeEvent.Type == "checkout.session.completed")
        {
            var session = stripeEvent.Data.Object as Stripe.Checkout.Session;

            if (Guid.TryParse(session?.ClientReferenceId, out var purchaseId))
            {
                var purchase = await _db.Purchases.FindAsync(purchaseId);

                if (purchase != null)
                {
                    purchase.Paid = true;
                    await _db.SaveChangesAsync();
                }
            }
        }
    }
}
