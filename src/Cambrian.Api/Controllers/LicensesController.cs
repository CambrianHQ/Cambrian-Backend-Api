using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("licenses")]
public class LicensesController : BaseController
{
    private readonly ILicenseService _licenses;

    public LicensesController(ILicenseService licenses)
    {
        _licenses = licenses;
    }

    /// <summary>
    /// Retrieve a license certificate by its ID.
    /// </summary>
    [HttpGet("{licenseId}")]
    public async Task<IActionResult> GetLicense(string licenseId)
    {
        var cert = await _licenses.GetByIdAsync(licenseId);
        if (cert is null)
            return NotFoundResponse("No license found.");
        return OkResponse(cert);
    }

    /// <summary>
    /// List all license certificates owned by the current user.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListMyLicenses()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
            return ErrorResponse("Not authenticated.");

        var certs = await _licenses.GetByBuyerAsync(userId);
        return OkResponse(certs);
    }
}
