using Cambrian.Application.DTOs.ApiKeys;

namespace Cambrian.Application.Interfaces;

public interface IApiKeyService
{
    Task<ApiKeyCreatedDto> CreateKeyAsync(string userId, CreateApiKeyRequest request);
    Task<List<ApiKeyDto>> GetKeysAsync(string userId);
    Task<bool> DeactivateKeyAsync(string userId, Guid keyId);
}
