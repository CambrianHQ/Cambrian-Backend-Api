using Cambrian.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cambrian.Persistence.Services;

public class HealthService : IHealthService
{
    private readonly CambrianDbContext _db;
    private readonly IHostEnvironment _env;
    private readonly IObjectStorage _storage;
    private readonly ILogger<HealthService> _logger;

    public HealthService(CambrianDbContext db, IHostEnvironment env, IObjectStorage storage, ILogger<HealthService> logger)
    {
        _db = db;
        _env = env;
        _storage = storage;
        _logger = logger;
    }

    public async Task<object> GetHealthAsync()
    {
        var dbHealthy = false;
        try
        {
            dbHealthy = await _db.Database.CanConnectAsync();
        }
        catch
        {
            // DB unreachable
        }

        var trackCount = 0;
        var purchaseCount = 0;
        var userCount = 0;
        var pendingPurchases = 0;

        if (dbHealthy)
        {
            try
            {
                trackCount = await _db.Tracks.CountAsync();
                purchaseCount = await _db.Purchases.CountAsync();
                userCount = await _db.Users.CountAsync();
                pendingPurchases = await _db.Purchases.CountAsync(p => p.Status == "pending");
            }
            catch
            {
                // stats unavailable — still report health
            }
        }

        _logger.LogInformation("Health check: status={Status} db={DbStatus}", dbHealthy ? "ok" : "degraded", dbHealthy ? "connected" : "unreachable");

        return new
        {
            status = dbHealthy ? "ok" : "degraded",
            timestamp = DateTime.UtcNow,
            environment = _env.EnvironmentName,
            database = dbHealthy ? "connected" : "unreachable",
            counts = new
            {
                tracks = trackCount,
                purchases = purchaseCount,
                users = userCount,
                pendingPurchases
            }
        };
    }

    public async Task<object> GetStorageDiagAsync()
    {
        var tracks = await _db.Tracks.OrderBy(t => t.CreatedAt).Take(3).ToListAsync();
        var results = new List<object>();
        foreach (var t in tracks)
        {
            bool fileExists = false;
            string? error = null;
            try
            {
                var file = await _storage.OpenReadAsync(t.AudioUrl ?? "");
                if (file is not null)
                {
                    fileExists = true;
                    file.Stream.Dispose();
                }
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }
            results.Add(new
            {
                trackId = t.Id,
                title = t.Title,
                audioUrl = t.AudioUrl,
                fileExists,
                error,
            });
        }

        return new
        {
            storageType = _storage.GetType().Name,
            tracks = results,
        };
    }

    public async Task<object> AuditAudioKeysAsync()
    {
        var tracks = await _db.Tracks
            .AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Title, t.AudioUrl })
            .ToListAsync();

        var missing = new List<object>();
        var checkedCount = 0;

        foreach (var t in tracks)
        {
            checkedCount++;
            if (string.IsNullOrWhiteSpace(t.AudioUrl))
            {
                missing.Add(new { trackId = t.Id, title = t.Title, audioUrl = t.AudioUrl, reason = "no_key" });
                continue;
            }

            try
            {
                var file = await _storage.OpenReadAsync(t.AudioUrl);
                if (file is null)
                    missing.Add(new { trackId = t.Id, title = t.Title, audioUrl = t.AudioUrl, reason = "not_found" });
                else
                    file.Dispose();
            }
            catch (Exception ex)
            {
                missing.Add(new { trackId = t.Id, title = t.Title, audioUrl = t.AudioUrl, reason = ex.Message });
            }
        }

        return new
        {
            storageType = _storage.GetType().Name,
            totalTracks = checkedCount,
            missingCount = missing.Count,
            okCount = checkedCount - missing.Count,
            missing,
        };
    }
}
