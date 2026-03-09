using Cambrian.Application.DTOs.Billing;

namespace Cambrian.Application.Interfaces;

public interface IBillingService
{
    Task<CheckoutResponse> CreateCheckoutAsync(BillingCheckoutRequest request, string userId);

    Task<BillingStatusResponse> GetStatusAsync(string userId);
}
