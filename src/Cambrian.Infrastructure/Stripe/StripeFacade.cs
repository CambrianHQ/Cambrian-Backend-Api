using Stripe;
using Stripe.Checkout;

namespace Cambrian.Infrastructure.Stripe;

public class StripeFacade
{
    public async Task<string> CreateCheckoutSessionAsync(int amountInCents, string trackName)
    {
        var options = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = "https://cambrian.app/checkout/success?session_id={CHECKOUT_SESSION_ID}",
            CancelUrl = "https://cambrian.app/checkout/cancel",
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
                            Name = trackName
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
}
