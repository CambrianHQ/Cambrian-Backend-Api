using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("upload")]
public class UploadController : ControllerBase
{
    private readonly ICatalogService _catalog;

    public UploadController(ICatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpPost]
    public async Task<IActionResult> Upload(UploadTrackRequest req)
    {
        var result = await _catalog.UploadTrackAsync(req);
        return Ok(result);
    }
}