using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Application.Interfaces;
using Cambrian.Api.Tools;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("licenses")]
[Authorize]
public class LicensesController : BaseController
{
    private readonly ILicenseService _licenses;
    private readonly ITrackRepository _tracks;
    private readonly UserManager<ApplicationUser> _users;

    public LicensesController(ILicenseService licenses, ITrackRepository tracks, UserManager<ApplicationUser> users)
    {
        _licenses = licenses;
        _tracks = tracks;
        _users = users;
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

    /// <summary>
    /// Publicly verify that a license certificate exists and is valid.
    /// Does not expose buyer email or other PII.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("verify/{licenseId}")]
    public async Task<IActionResult> VerifyLicense(string licenseId)
    {
        var cert = await _licenses.GetByIdAsync(licenseId);
        if (cert is null)
            return NotFoundResponse("License not found.");

        string? trackTitle = null;
        if (Guid.TryParse(cert.TrackId, out var trackGuid))
        {
            var track = await _tracks.GetByIdAsync(trackGuid);
            trackTitle = track?.Title;
        }

        var buyer = await _users.FindByIdAsync(cert.BuyerId);
        var creator = await _users.FindByIdAsync(cert.CreatorId);

        return OkResponse(new
        {
            valid = true,
            licenseId = cert.LicenseId,
            trackTitle,
            licenseType = cert.LicenseType,
            usageType = cert.UsageType,
            buyerDisplayName = buyer?.DisplayName ?? "Unknown",
            creatorDisplayName = creator?.DisplayName ?? "Unknown",
            issuedAt = cert.IssuedAt
        });
    }

}
