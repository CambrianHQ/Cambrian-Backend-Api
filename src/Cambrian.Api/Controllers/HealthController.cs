using Cambrian.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("health")]
public class HealthController : ControllerBase
{
    private readonly CambrianDbContext _db;
    private readonly IWebHostEnvironment _env;

    public HealthController(CambrianDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
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
}
