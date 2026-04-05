using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("analytics")]
public class SearchAnalyticsController : BaseController
{
    private readonly ISearchAnalyticsService _searchAnalyticsService;

    public SearchAnalyticsController(ISearchAnalyticsService searchAnalyticsService)
    {
        _searchAnalyticsService = searchAnalyticsService;
    }

    /// <summary>Get trending searches and zero-result queries from the last 7 days.</summary>
    [HttpGet("trending-searches")]
    [Authorize(Roles = "Admin,Creator")]
    public async Task<IActionResult> GetTrendingSearches()
    {
        var result = await _searchAnalyticsService.GetTrendingSearchesAsync(7);
        return OkResponse(result);
    }
}
