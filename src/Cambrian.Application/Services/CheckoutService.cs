using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class CheckoutService : ICheckoutService
{
    public Task<CheckoutResponse> CreateCheckoutAsync(CheckoutRequest request)
    {
        var response = new CheckoutResponse
        {
            CheckoutUrl = $"https://checkout.cambrian.local/{request.TrackId}",
            Status = "created"
        };

        return Task.FromResult(response);
    }
}
