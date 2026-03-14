namespace Cambrian.Application.Interfaces;

/// <summary>
/// Service for health / diagnostics checks (DB connectivity, entity counts, storage).
/// </summary>
public interface IHealthService
{
    /// <summary>Runs a health check including DB connectivity and entity counts.</summary>
    Task<object> GetHealthAsync();

    /// <summary>Checks storage state for a sample of tracks.</summary>
    Task<object> GetStorageDiagAsync();
}
