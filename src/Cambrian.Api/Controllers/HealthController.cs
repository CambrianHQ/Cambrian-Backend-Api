using Cambrian.Application.Interfaces;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("health")]
public class HealthController : ControllerBase
{
    private readonly CambrianDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IObjectStorage _storage;
    private readonly ILogger<HealthController> _logger;

    public HealthController(CambrianDbContext db, IWebHostEnvironment env, IObjectStorage storage, ILogger<HealthController> logger)
    {
        _db = db;
        _env = env;
        _storage = storage;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
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

        var result = new
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

        _logger.LogInformation("Health check: status={Status} db={DbStatus}", result.status, result.database);

        // Always return 200 so Render health-check passes and routes traffic
        return Ok(result);
    }

    /// <summary>
    /// Diagnostic endpoint to check audio storage state for a sample track.
    /// Returns the raw AudioUrl and whether the file exists in storage.
    /// </summary>
    [HttpGet("storage")]
    public async Task<IActionResult> StorageDiag()
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
        return Ok(new
        {
            storageType = _storage.GetType().Name,
            tracks = results,
        });
    }
}
