namespace Cambrian.Api.Services;

public interface IStripeService
{
    Task<string> CreateCheckoutSession(decimal amount);
}
