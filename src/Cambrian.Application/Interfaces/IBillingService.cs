using Cambrian.Application.DTOs.Billing;

namespace Cambrian.Application.Interfaces;

public interface IBillingService
{
    Task<CheckoutResponse> CreateCheckoutAsync(string tier, string userId, string frontendUrl);

    Task<BillingStatusResponse> GetStatusAsync(string userId);
}
