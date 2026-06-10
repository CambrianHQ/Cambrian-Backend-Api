using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

/// <summary>
/// "The Scene" weekly charts. Reads are public; aggregation is admin-triggered
/// on demand (residue R17) — there is no scheduled job yet, so the admin POST
/// is how the chart is (re)populated and made testable.
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
    public async Task<IActionResult> Weekly(CancellationToken ct)
    {
        var chart = await _charts.GetCurrentAsync(ct);
        return OkResponse(chart);
    }

    /// <summary>POST /admin/charts/aggregate — recompute the weekly chart now.</summary>
    [HttpPost("/admin/charts/aggregate")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Aggregate(CancellationToken ct)
    {
        _logger.LogInformation("EVENT: WeeklyChartAggregationTriggered");
        var chart = await _charts.AggregateAsync(ct);
        _logger.LogInformation("EVENT: WeeklyChartAggregated entries:{Count}", chart.Entries.Count);
        return OkResponse(chart);
    }
}
