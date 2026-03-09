using Cambrian.Application.DTOs.Health;
using Cambrian.Application.Interfaces;
using Cambrian.Persistence;
using Microsoft.Extensions.Hosting;

namespace Cambrian.Infrastructure.Health;

public class DatabaseHealthService : IHealthService
{
    private readonly CambrianDbContext _db;
    private readonly IHostEnvironment _environment;

    public DatabaseHealthService(CambrianDbContext db, IHostEnvironment environment)
    {
        _db = db;
        _environment = environment;
    }

    public async Task<HealthStatusResponse> GetStatusAsync()
    {
        var dbHealthy = false;

        try
        {
            dbHealthy = await _db.Database.CanConnectAsync();
        }
        catch
        {
            // Report degraded health instead of bubbling transient connection issues.
        }

        return new HealthStatusResponse
        {
            Status = dbHealthy ? "ok" : "degraded",
            Timestamp = DateTime.UtcNow,
            Environment = _environment.EnvironmentName,
            Database = dbHealthy ? "connected" : "unreachable"
        };
    }
}
