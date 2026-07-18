using Cambrian.Api.Common;
using Cambrian.Application.DTOs.Charts;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

/// <summary>
/// "The Scene" weekly charts. Reads are public, stale/missing charts are
/// refreshed lazily, a background worker keeps them current, and admins retain
/// an explicit recompute endpoint.
/// </summary>
public sealed class ChartsController : BaseController
{
    private readonly IWeeklyChartService _charts;
    private readonly ILogger<ChartsController> _logger;

    public ChartsController(IWeeklyChartService charts, ILogger<ChartsController> logger)
    {
        _charts = charts;
        _logger = logger;
    }

    /// <summary>GET /api/charts/weekly — the canonical weekly chart.</summary>
    [AllowAnonymous]
    [HttpGet("/api/charts/weekly")]
    [ProducesResponseType(typeof(ApiResponse<WeeklyChartsResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WeeklyChartsResponse>>> Weekly(CancellationToken ct)
    {
        var chart = await _charts.GetCurrentAsync(ct);
        return Ok(ApiResponse<WeeklyChartsResponse>.Ok(chart));
    }

    /// <summary>
    /// GET /api/charts/weekly/archive — completed chart weeks, newest first.
    /// The running week is never listed here (it lives on /api/charts/weekly).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/api/charts/weekly/archive")]
    public async Task<IActionResult> Archive([FromQuery] int limit = 104, CancellationToken ct = default)
    {
        var index = await _charts.GetArchiveIndexAsync(Math.Clamp(limit, 1, 520), ct);
        return OkResponse(index);
    }

    /// <summary>
    /// GET /api/charts/weekly/archive/{isoWeek} — the final Top 50 of one
    /// completed week, e.g. isoWeek = "2026-w28". 404 for unknown, running,
    /// or future weeks; the archive is a permanent record of finished weeks.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/api/charts/weekly/archive/{isoWeek}")]
    public async Task<IActionResult> ArchivedWeek(string isoWeek, CancellationToken ct)
    {
        var weekStart = Application.Services.WeeklyChartService.ParseIsoWeekKey(isoWeek);
        if (weekStart is null) return ErrorResponse("isoWeek must look like '2026-w28'.");

        var chart = await _charts.GetArchivedWeekAsync(weekStart.Value, ct);
        return chart is null
            ? NotFoundResponse("No archived chart for that week.")
            : OkResponse(chart);
    }

    /// <summary>POST /admin/charts/aggregate — recompute the weekly chart now.</summary>
    [HttpPost("/admin/charts/aggregate")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(ApiResponse<WeeklyChartsResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WeeklyChartsResponse>>> Aggregate(CancellationToken ct)
    {
        _logger.LogInformation("EVENT: WeeklyChartAggregationTriggered");
        var chart = await _charts.AggregateAsync(ct);
        _logger.LogInformation("EVENT: WeeklyChartAggregated entries:{Count}", chart.Entries.Count);
        return Ok(ApiResponse<WeeklyChartsResponse>.Ok(chart));
    }
}
