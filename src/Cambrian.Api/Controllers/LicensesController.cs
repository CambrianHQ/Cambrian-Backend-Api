using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Application.Interfaces;
using Cambrian.Api.Tools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("licenses")]
[Authorize]
public class LicensesController : BaseController
{
    private readonly ILicenseService _licenses;
    private readonly ITrackRepository _tracks;

    public LicensesController(ILicenseService licenses, ITrackRepository tracks)
    {
        _licenses = licenses;
        _tracks = tracks;
    }

    /// <summary>
    /// Retrieve a license certificate by its ID.
    /// Only returns certificates owned by the authenticated user.
    /// </summary>
    [HttpGet("{licenseId}")]
    public async Task<IActionResult> GetLicense(string licenseId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var cert = await _licenses.GetByIdAsync(licenseId);
        if (cert is null)
            return NotFoundResponse("No license found.");

        // Scope to current user — prevent enumeration of other users' licenses
        if (cert.BuyerId != userId)
            return NotFoundResponse("No license found.");

        return OkResponse(cert);
    }

    /// <summary>
    /// List all license certificates owned by the current user.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListMyLicenses()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return ErrorResponse("Not authenticated.");

        var certs = await _licenses.GetByBuyerAsync(userId);
        return OkResponse(certs);
    }

    /// <summary>
    /// Download a license certificate as a PDF file.
    /// </summary>
    [HttpGet("{licenseId}/pdf")]
    public async Task<IActionResult> DownloadPdf(string licenseId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var cert = await _licenses.GetByIdAsync(licenseId);
        if (cert is null || cert.BuyerId != userId)
            return NotFoundResponse("No license found.");

        // Resolve track title for the PDF
        string? trackTitle = null;
        if (Guid.TryParse(cert.TrackId, out var trackGuid))
        {
            var track = await _tracks.GetByIdAsync(trackGuid);
            trackTitle = track?.Title;
        }

        var pdfBytes = LicensePdfGenerator.Generate(cert, trackTitle);
        var safeTitle = FilenameHelper.SanitizeFilename(trackTitle ?? "track");
        var filename = $"license-{safeTitle}-{licenseId[..8]}.pdf";

        return File(pdfBytes, "application/pdf", filename);
    }

}
