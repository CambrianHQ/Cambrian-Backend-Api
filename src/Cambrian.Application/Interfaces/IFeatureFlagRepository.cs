using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IFeatureFlagRepository
{
    Task<IReadOnlyList<FeatureFlag>> GetAllAsync();

    Task<FeatureFlag?> GetByNameAsync(string name);

    Task<FeatureFlag> UpsertAsync(string name, bool enabled, int rolloutPercentage = 100);

    Task DeleteAsync(string name);

    Task<bool> IsEnabledAsync(string name, string? userId = null);
}
