using Cambrian.Application.DTOs.Pricing;

namespace Cambrian.Application.Interfaces;

public interface IPricingIntelligenceService
{
    Task<PricingIntelligenceDto?> GetGenrePricingAsync(string genre);
    Task<List<CreatorPricingPositionDto>> GetCreatorPositionAsync(string creatorUserId);
}
