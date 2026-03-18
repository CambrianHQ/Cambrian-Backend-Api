using Cambrian.Application.DTOs.CreatorProfile;

namespace Cambrian.Application.Interfaces;

public interface IStorefrontService
{
    /// <summary>
    /// Returns the full public storefront for a creator by slug.
    /// Includes profile, stats, pinned tracks, collections, and storefront-safe tracks.
    /// </summary>
    Task<StorefrontResponse?> GetStorefrontAsync(string slug);
}
