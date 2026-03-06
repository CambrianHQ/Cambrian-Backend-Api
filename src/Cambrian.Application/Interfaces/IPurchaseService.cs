using Cambrian.Application.DTOs.Purchases;

namespace Cambrian.Application.Interfaces;

public interface IPurchaseService
{
    Task<PurchaseResponse> CreateAsync(PurchaseCreateRequest request, string userId);

    Task<IReadOnlyCollection<PurchaseResponse>> GetByBuyerAsync(string userId);

    Task CreditCreatorAsync(CreditCreatorRequest request);
}
