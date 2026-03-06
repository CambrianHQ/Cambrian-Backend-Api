using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class UploadController : ControllerBase
{
    private readonly ICatalogService _catalog;

    public UploadController(ICatalogService catalog)
    {
        _catalog = catalog;
    }

    [Authorize(Roles = "Creator")]
    [HttpPost("upload")]
    public async Task<IActionResult> Upload([FromForm] UploadTrackRequest request)
    {
        var result = await _catalog.UploadTrackAsync(request);
        return Ok(result);
    }
}