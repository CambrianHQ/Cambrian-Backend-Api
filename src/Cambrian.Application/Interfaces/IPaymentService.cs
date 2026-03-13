using Cambrian.Application.DTOs.Payments;

namespace Cambrian.Application.Interfaces;

public interface IPaymentService
{
    Task<PaymentCheckoutResponse> CreateCheckoutAsync(PaymentCheckoutRequest request, string userId, string? customerEmail = null);

    Task<PaymentStateResponse> GetStateAsync();

    Task<PaymentResultResponse> GetResultAsync(string? status, string? trackId);

    Task ProcessAsync(PaymentProcessRequest request, string userId);
}
