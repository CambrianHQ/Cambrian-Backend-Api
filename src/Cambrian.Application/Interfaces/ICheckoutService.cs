using Cambrian.Application.DTOs.Checkout;

namespace Cambrian.Application.Interfaces;

public interface ICheckoutService
{
    Task<CheckoutResponse> CreateCheckoutAsync(CheckoutRequest request);
}