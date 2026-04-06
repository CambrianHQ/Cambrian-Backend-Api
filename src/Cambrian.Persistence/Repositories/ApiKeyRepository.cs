using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class ApiKeyRepository : IApiKeyRepository
{
    private readonly CambrianDbContext _db;

    public ApiKeyRepository(CambrianDbContext db) => _db = db;

    public async Task<ApiKey> CreateAsync(ApiKey key)
    {
        _db.ApiKeys.Add(key);
        await _db.SaveChangesAsync();
        return key;
    }

    public Task<ApiKey?> GetByHashAsync(string keyHash)
        => _db.ApiKeys
              .AsNoTracking()
              .FirstOrDefaultAsync(k => k.KeyHash == keyHash && k.IsActive);

    public async Task<IReadOnlyList<ApiKey>> GetByUserIdAsync(string userId)
        => await _db.ApiKeys
                    .AsNoTracking()
                    .Where(k => k.UserId == userId && k.IsActive)
                    .OrderByDescending(k => k.CreatedAt)
                    .ToListAsync();

    public async Task<bool> DeactivateAsync(Guid keyId, string userId)
    {
        var rows = await _db.ApiKeys
            .Where(k => k.Id == keyId && k.UserId == userId && k.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.IsActive, false));
        return rows > 0;
    }

    public Task UpdateLastUsedAsync(Guid keyId)
        => _db.ApiKeys
              .Where(k => k.Id == keyId)
              .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTime.UtcNow));
}
