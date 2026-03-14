using System.Security.Claims;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("")]
public class UploadController : BaseController
{
    private readonly IUploadService _upload;
    private readonly ILogger<UploadController> _logger;

    public UploadController(IUploadService upload, ILogger<UploadController> logger)
    {
        _upload = upload;
        _logger = logger;
    }

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("upload")]
    [DisableRequestSizeLimit] // Allow large audio files — validated in UploadService
    public async Task<IActionResult> Upload([FromForm] UploadTrackRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation("EVENT: UploadStarted userId:{UserId} title:{Title}", userId, request.Title);
        request.CreatorId = userId;
        var result = await _upload.Upload(request);
        _logger.LogInformation("EVENT: UploadCompleted userId:{UserId} trackId:{TrackId} title:{Title}", userId, result, request.Title);
        return CreatedResponse(result, "Track uploaded successfully.");
    }
}