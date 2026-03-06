namespace Cambrian.Application.Interfaces;

public interface IBillingService
{
    Task<string> CreateBillingPortalAsync(Guid userId, CancellationToken cancellationToken = default);
}
