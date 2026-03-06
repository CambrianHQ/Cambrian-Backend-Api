using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public sealed class BillingService : IBillingService
{
    public Task<string> CreateBillingPortalAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult($"https://billing.stripe.test/portal/{userId:N}");
    }
}
