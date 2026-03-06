using Cambrian.Api.Data;
using Cambrian.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stripe;
using Stripe.Checkout;

namespace Cambrian.Api.Services;

public class StripeWebhookService
{
    private readonly ApplicationDbContext _db;
    private readonly string _webhookSecret;

    // Platform fee percentage (20%)
    private const decimal PlatformFeeRate = 0.20m;

    public StripeWebhookService(ApplicationDbContext db, IConfiguration configuration)
    {
        _db = db;
        _webhookSecret = configuration["Stripe:WebhookSecret"] ?? "";
    }

    public async Task Handle(string json, string signature)
    {
        Event stripeEvent;

        if (!string.IsNullOrEmpty(_webhookSecret) && !string.IsNullOrEmpty(signature))
        {
            stripeEvent = EventUtility.ConstructEvent(json, signature, _webhookSecret);
        }
        else
        {
            // Dev fallback — skip signature verification when no secret configured
            stripeEvent = EventUtility.ParseEvent(json);
        }

        if (stripeEvent.Type == EventTypes.CheckoutSessionCompleted)
        {
            await HandleCheckoutCompleted(stripeEvent);
        }
    }

    private async Task HandleCheckoutCompleted(Event stripeEvent)
    {
        var session = stripeEvent.Data.Object as Session;

        if (!Guid.TryParse(session?.ClientReferenceId, out var purchaseId))
            return;

        var purchase = await _db.Purchases.FindAsync(purchaseId);
        if (purchase is null || purchase.Paid)
            return;

        // 1. Mark purchase as paid
        purchase.Paid = true;

        // 2. Add track to user's library
        var alreadyInLibrary = await _db.Library
            .AnyAsync(l => l.UserId == purchase.UserId && l.TrackId == purchase.TrackId);

        if (!alreadyInLibrary)
        {
            _db.Library.Add(new LibraryItem
            {
                Id = Guid.NewGuid(),
                UserId = purchase.UserId,
                TrackId = purchase.TrackId,
                PurchaseId = purchase.Id,
                AddedAt = DateTime.UtcNow
            });
        }

        // 3. Update creator's pending balance (purchase amount minus platform fee)
        var track = await _db.Tracks.FindAsync(purchase.TrackId);
        if (track is not null)
        {
            var creatorNet = purchase.Amount * (1 - PlatformFeeRate);

            var balance = await _db.CreatorBalances
                .FirstOrDefaultAsync(b => b.CreatorId == track.CreatorId);

            if (balance is null)
            {
                _db.CreatorBalances.Add(new CreatorBalance
                {
                    Id = Guid.NewGuid(),
                    CreatorId = track.CreatorId,
                    PendingBalance = creatorNet,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                balance.PendingBalance += creatorNet;
                balance.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();
    }
}
