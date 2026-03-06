using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class CatalogController : ControllerBase
{
    private readonly ICatalogService _catalog;

    public CatalogController(ICatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpGet("discover")]
    public async Task<IActionResult> Discover(int page = 1, int pageSize = 20, string? genre = null, string? search = null)
    {
        var result = await _catalog.GetDiscoverAsync(page, pageSize, genre, search);
        return Ok(result);
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog(int page = 1, int pageSize = 50, string? genre = null, string? search = null)
    {
        var result = await _catalog.GetCatalogAsync(page, pageSize, genre, search);
        return Ok(result);
    }

    [HttpGet("tracks/{trackId}")]
    public async Task<IActionResult> GetTrack(string trackId)
    {
        var result = await _catalog.GetTrackAsync(trackId);
        return result is null ? NotFound() : Ok(result);
    }
}