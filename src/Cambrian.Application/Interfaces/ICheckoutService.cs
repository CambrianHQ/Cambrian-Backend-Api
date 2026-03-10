using System.Security.Claims;
using Cambrian.Application.DTOs.Checkout;

namespace Cambrian.Application.Interfaces;

public interface ICheckoutService
{
    Task<CheckoutResponse> CreateCheckoutAsync(CheckoutRequest request, ClaimsPrincipal user);

    /// <summary>
    /// Confirm a completed Stripe checkout session for a track purchase.
    /// Retrieves the session from Stripe, verifies payment, then idempotently
    /// creates Purchase + LibraryItem + creator wallet credit.
    /// </summary>
    Task<CheckoutConfirmResponse> ConfirmAsync(string sessionId, string userId);
}