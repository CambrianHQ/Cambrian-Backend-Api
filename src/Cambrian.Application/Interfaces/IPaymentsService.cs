using Cambrian.Application.DTOs.Payments;

namespace Cambrian.Application.Interfaces;

public interface IPaymentsService
{
    Task<object> CreatePaymentCheckoutAsync(string trackId, string userId);

    Task<PaymentStateResponse> GetStateAsync(string purchaseId);

    Task<PaymentStateResponse> GetResultAsync(string purchaseId);

    Task<PaymentStateResponse> ProcessAsync(PaymentProcessRequest request, string userId);
}
