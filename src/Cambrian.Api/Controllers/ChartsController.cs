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
