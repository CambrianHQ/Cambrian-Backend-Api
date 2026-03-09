using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("download")]
[Authorize]
public class DownloadController : BaseController
{
    private readonly IDownloadService _download;

    public DownloadController(IDownloadService download)
    {
        _download = download;
    }

    [HttpGet("{trackId}")]
    public async Task<IActionResult> Download(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            return ErrorResponse("trackId must be a valid GUID.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await _download.GetDownloadUrlAsync(id, userId);
        return OkResponse(result);
    }

    [HttpGet("{trackId}/signed")]
    public async Task<IActionResult> SignedUrl(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            return ErrorResponse("trackId must be a valid GUID.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await _download.GetSignedUrlAsync(id, userId);
        return OkResponse(result);
    }
}
