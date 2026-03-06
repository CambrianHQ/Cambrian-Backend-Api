using Cambrian.Api.Filters;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;

namespace Cambrian.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCambrianApiServices(this IServiceCollection services)
    {
        services.AddScoped<ValidateModelFilter>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<ILibraryService, LibraryService>();
        services.AddScoped<ICheckoutService, CheckoutService>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IPayoutService, PayoutService>();
        services.AddScoped<IAdminService, AdminService>();
        return services;
    }
}
