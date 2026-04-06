using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IApiKeyRepository
{
    Task<ApiKey> CreateAsync(ApiKey key);
    Task<ApiKey?> GetByHashAsync(string keyHash);
    Task<IReadOnlyList<ApiKey>> GetByUserIdAsync(string userId);
    Task<bool> DeactivateAsync(Guid keyId, string userId);
    Task UpdateLastUsedAsync(Guid keyId);
}
