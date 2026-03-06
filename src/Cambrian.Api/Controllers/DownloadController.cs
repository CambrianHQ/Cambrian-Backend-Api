using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("download")]
[Authorize]
public class DownloadController : BaseController
{
    [HttpGet("{trackId}")]
    public IActionResult Download(string trackId)
    {
        if (!Guid.TryParse(trackId, out _))
            return ErrorResponse("trackId must be a valid GUID.");
        return OkResponse(new { url = (string?)null });
    }

    [HttpGet("{trackId}/signed")]
    public IActionResult SignedUrl(string trackId)
    {
        if (!Guid.TryParse(trackId, out _))
            return ErrorResponse("trackId must be a valid GUID.");
        return OkResponse(new { signedUrl = (string?)null, expiresAt = DateTime.UtcNow.AddMinutes(15) });
    }
}
