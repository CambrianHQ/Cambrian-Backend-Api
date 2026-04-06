using System.Security.Cryptography;
using System.Text;
using Cambrian.Application.DTOs.ApiKeys;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class ApiKeyService : IApiKeyService
{
    private readonly IApiKeyRepository _repo;

    public ApiKeyService(IApiKeyRepository repo) => _repo = repo;

    public async Task<(string RawKey, ApiKeyListItemDto Metadata)> GenerateAsync(string userId, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new ArgumentException("Key name must be between 1 and 100 characters.");

        // Format: cbr_ + 32 random bytes as lowercase hex = 68 chars total
        var rawKey = "cbr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLower();
        var keyHash = HashKey(rawKey);
        var keyPrefix = rawKey[..8]; // "cbr_XXXX"

        var entity = new ApiKey
        {
            Id        = Guid.NewGuid(),
            UserId    = userId,
            KeyHash   = keyHash,
            KeyPrefix = keyPrefix,
            Name      = name.Trim(),
            CreatedAt = DateTime.UtcNow,
            IsActive  = true,
        };

        var created = await _repo.CreateAsync(entity);
        return (rawKey, ToDto(created));
    }

    public async Task<IReadOnlyList<ApiKeyListItemDto>> ListAsync(string userId)
    {
        var keys = await _repo.GetByUserIdAsync(userId);
        return keys.Select(ToDto).ToList();
    }

    public async Task<bool> RevokeAsync(Guid keyId, string userId)
        => await _repo.DeactivateAsync(keyId, userId);

    /// <summary>SHA-256 hex of the raw key string. Used for storage and lookup.</summary>
    public static string HashKey(string rawKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLower();

    private static ApiKeyListItemDto ToDto(ApiKey k) => new()
    {
        Id          = k.Id,
        KeyPrefix   = k.KeyPrefix,
        Name        = k.Name,
        CreatedAt   = k.CreatedAt,
        LastUsedAt  = k.LastUsedAt,
    };
}
