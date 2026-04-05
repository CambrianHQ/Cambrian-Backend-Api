using System.Security.Cryptography;
using Cambrian.Application.DTOs.ApiKeys;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Services;

public class ApiKeyService : IApiKeyService
{
    private readonly CambrianDbContext _db;

    public ApiKeyService(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<ApiKeyCreatedDto> CreateKeyAsync(string userId, CreateApiKeyRequest request)
    {
        var rawKey = GenerateSecureKey();
        var keyHash = HashKey(rawKey);
        var keySuffix = rawKey[^4..];

        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            KeyHash = keyHash,
            KeySuffix = keySuffix,
            Name = request.Name,
            RateLimit = request.RateLimit,
            CreatedAt = DateTime.UtcNow
        };

        _db.ApiKeys.Add(apiKey);
        await _db.SaveChangesAsync();

        return new ApiKeyCreatedDto
        {
            Id = apiKey.Id,
            Name = apiKey.Name,
            Key = rawKey,
            CreatedAt = apiKey.CreatedAt,
            RateLimit = apiKey.RateLimit
        };
    }

    public async Task<List<ApiKeyDto>> GetKeysAsync(string userId)
    {
        return await _db.ApiKeys
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeyDto
            {
                Id = k.Id,
                Name = k.Name,
                KeyMasked = "****" + k.KeySuffix,
                CreatedAt = k.CreatedAt,
                LastUsedAt = k.LastUsedAt,
                IsActive = k.IsActive,
                RateLimit = k.RateLimit
            })
            .ToListAsync();
    }

    public async Task<bool> DeactivateKeyAsync(string userId, Guid keyId)
    {
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId);
        if (key is null) return false;

        key.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    private static string GenerateSecureKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string HashKey(string rawKey)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
