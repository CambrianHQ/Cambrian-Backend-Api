using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("admin/media-reconciliation")]
[Authorize(Roles = "Admin")]
public sealed class MediaReconciliationController : ControllerBase
{
    private readonly IMediaReconciliationService _service;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MediaReconciliationController> _logger;

    public MediaReconciliationController(
        IMediaReconciliationService service,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<MediaReconciliationController> logger)
    {
        _service = service;
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
    }

    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns([FromQuery] int take = 20, CancellationToken ct = default) =>
        Ok(new { success = true, data = await _service.GetRunsAsync(take, ct) });

    [HttpGet("runs/{runId:guid}")]
    public async Task<IActionResult> GetRun(Guid runId, CancellationToken ct = default)
    {
        var report = await _service.GetRunAsync(runId, ct);
        return report is null
            ? NotFound(new { success = false, error = "reconciliation_run_not_found" })
            : Ok(new { success = true, data = report });
    }

    public sealed record RunRequest(bool Remediate = false);

    [HttpPost("runs")]
    public async Task<IActionResult> Run([FromBody] RunRequest? request, CancellationToken ct = default)
    {
        if (!MediaReconciliationRunGuard.TryAcquire())
            return Conflict(new { success = false, error = "reconciliation_run_already_active" });

        MediaReconciliationSummary created;
        try
        {
            created = await _service.CreateRunAsync(request?.Remediate ?? false, ct);
        }
        catch
        {
            MediaReconciliationRunGuard.Release();
            throw;
        }

        // The scan must survive the HTTP request: fresh DI scope, cancellation
        // bound to host shutdown rather than the request abort token.
        var stoppingToken = _lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IMediaReconciliationService>();
                await service.ExecuteRunAsync(created.RunId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background media reconciliation run {RunId} failed", created.RunId);
            }
            finally
            {
                MediaReconciliationRunGuard.Release();
            }
        }, CancellationToken.None);

        return AcceptedAtAction(nameof(GetRun), new { runId = created.RunId }, new { success = true, data = created });
    }
}
