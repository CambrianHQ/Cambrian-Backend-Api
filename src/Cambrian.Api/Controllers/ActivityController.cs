using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("activity")]
public sealed class ActivityController : ControllerBase
{
    private readonly IActivityService _activityService;

    public ActivityController(IActivityService activityService)
    {
        _activityService = activityService;
    }

    [HttpGet("new")]
    [ProducesResponseType(typeof(IReadOnlyList<ActivityItemResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ActivityItemResponse>>> GetNew(CancellationToken ct)
    {
        var result = await _activityService.GetNewAsync(ct);
        return Ok(result);
    }

    [HttpGet("sales")]
    [ProducesResponseType(typeof(IReadOnlyList<ActivityItemResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ActivityItemResponse>>> GetSales(CancellationToken ct)
    {
        var result = await _activityService.GetSalesAsync(ct);
        return Ok(result);
    }

    [HttpGet("trending")]
    [ProducesResponseType(typeof(IReadOnlyList<TrendingTrackResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TrendingTrackResponse>>> GetTrending(CancellationToken ct)
    {
        var result = await _activityService.GetTrendingAsync(ct);
        return Ok(result);
    }
}
