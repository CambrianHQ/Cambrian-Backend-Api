namespace Cambrian.Application.Interfaces;

public interface IFeatureFlagService
{
    Task<bool> IsEnabledAsync(string key, CancellationToken ct);
}
