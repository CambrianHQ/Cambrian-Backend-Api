using System.Security.Claims;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("")]
public class UploadController : BaseController
{
    private readonly IUploadService _upload;

    public UploadController(IUploadService upload)
    {
        _upload = upload;
    }

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("upload")]
    public async Task<IActionResult> Upload([FromForm] UploadTrackRequest request)
    {
        request.CreatorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var result = await _upload.Upload(request);
        return CreatedResponse(result, "Track uploaded successfully.");
    }
}