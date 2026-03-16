using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class FeatureFlagRepository : IFeatureFlagRepository
{
    private readonly CambrianDbContext _db;

    public FeatureFlagRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<FeatureFlag>> GetAllAsync()
    {
        return await _db.FeatureFlags.OrderBy(f => f.Name).ToListAsync();
    }

    public async Task<FeatureFlag?> GetByNameAsync(string name)
    {
        return await _db.FeatureFlags.FirstOrDefaultAsync(f => f.Name == name);
    }

    public async Task<FeatureFlag> UpsertAsync(string name, bool enabled, int rolloutPercentage = 100)
    {
        var flag = await _db.FeatureFlags.FirstOrDefaultAsync(f => f.Name == name);

        if (flag is null)
        {
            flag = new FeatureFlag
            {
                Id = Guid.NewGuid(),
                Name = name,
                Enabled = enabled,
                RolloutPercentage = Math.Clamp(rolloutPercentage, 0, 100),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.FeatureFlags.Add(flag);
        }
        else
        {
            flag.Enabled = enabled;
            flag.RolloutPercentage = Math.Clamp(rolloutPercentage, 0, 100);
            flag.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return flag;
    }

    public async Task DeleteAsync(string name)
    {
        var flag = await _db.FeatureFlags.FirstOrDefaultAsync(f => f.Name == name);
        if (flag is not null)
        {
            _db.FeatureFlags.Remove(flag);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> IsEnabledAsync(string name, string? userId = null)
    {
        var flag = await _db.FeatureFlags.FirstOrDefaultAsync(f => f.Name == name);
        if (flag is null || !flag.Enabled)
            return false;

        if (flag.RolloutPercentage >= 100)
            return true;

        // Deterministic rollout based on userId hash so the same user
        // always gets the same result for a given flag.
        if (string.IsNullOrEmpty(userId))
            return false;

        var hash = Math.Abs(HashCode.Combine(name, userId)) % 100;
        return hash < flag.RolloutPercentage;
    }
}
