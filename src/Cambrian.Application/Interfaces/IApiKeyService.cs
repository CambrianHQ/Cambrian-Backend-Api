using Cambrian.Application.DTOs.ApiKeys;

namespace Cambrian.Application.Interfaces;

public interface IApiKeyService
{
    /// <summary>
    /// Generates a new API key for the given user. Returns the raw key (shown once) and metadata.
    /// </summary>
    Task<(string RawKey, ApiKeyListItemDto Metadata)> GenerateAsync(string userId, string name);

    Task<IReadOnlyList<ApiKeyListItemDto>> ListAsync(string userId);

    /// <summary>Returns false if key not found or does not belong to userId.</summary>
    Task<bool> RevokeAsync(Guid keyId, string userId);
}
