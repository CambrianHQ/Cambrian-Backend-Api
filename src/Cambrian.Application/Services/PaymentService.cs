using Cambrian.Application.DTOs.Payments;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class PaymentService : IPaymentService
{
    public Task<PaymentCheckoutResponse> CreateCheckoutAsync(PaymentCheckoutRequest request)
    {
        // TODO: Create Stripe checkout session
        var response = new PaymentCheckoutResponse
        {
            CheckoutUrl = $"https://checkout.stripe.com/pay/{request.TrackId}"
        };

        return Task.FromResult(response);
    }

    public Task<PaymentStateResponse> GetStateAsync()
    {
        // TODO: Return actual payment state from database
        var response = new PaymentStateResponse
        {
            Status = "pending",
            PurchaseIds = [],
            ProcessedEventIds = []
        };

        return Task.FromResult(response);
    }

    public Task<PaymentResultResponse> GetResultAsync(string? status, string? trackId)
    {
        // TODO: Look up payment result from database
        var response = new PaymentResultResponse
        {
            Status = status ?? "pending",
            Duplicate = false
        };

        return Task.FromResult(response);
    }

    public Task ProcessAsync(PaymentProcessRequest request)
    {
        // TODO: Process payment via Stripe
        return Task.CompletedTask;
    }
}
