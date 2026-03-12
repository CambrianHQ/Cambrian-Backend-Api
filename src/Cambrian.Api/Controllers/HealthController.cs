using Cambrian.Application.Interfaces;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Controllers;

[Route("health")]
public class HealthController : ControllerBase
{
    private readonly CambrianDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IObjectStorage _storage;

    public HealthController(CambrianDbContext db, IWebHostEnvironment env, IObjectStorage storage)
    {
        _db = db;
        _env = env;
        _storage = storage;
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

        var result = new
        {
            status = dbHealthy ? "ok" : "degraded",
            timestamp = DateTime.UtcNow,
            environment = _env.EnvironmentName,
            database = dbHealthy ? "connected" : "unreachable"
        };

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
