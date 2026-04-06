using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers.v1;

/// <summary>
/// Public versioned API — license purchase initiation and verification.
/// </summary>
[ApiController]
[Route("api/v1")]
[EnableRateLimiting("api_key_free")]
public class LicensesV1Controller : ControllerBase
{
    private readonly ICheckoutService _checkout;
    private readonly ILicenseService _licenses;

    public LicensesV1Controller(ICheckoutService checkout, ILicenseService licenses)
    {
        _checkout = checkout;
        _licenses = licenses;
    }

    /// <summary>
    /// Initiate a Stripe checkout session for a track license.
    /// Returns a Stripe-hosted checkout URL. Requires authentication.
    /// </summary>
    [HttpPost("licenses")]
    [Authorize]
    public async Task<IActionResult> PurchaseLicense([FromBody] CheckoutRequest request)
    {
        try
        {
            var result = await _checkout.CreateCheckoutAsync(request, User);
            return Ok(new
            {
                success     = true,
                checkoutUrl = result.CheckoutUrl,
                status      = result.Status,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Verify a license certificate. Public — no authentication required.
    /// Use this to confirm that a buyer owns a valid license for a track.
    /// </summary>
    [HttpGet("licenses/{id}/verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyLicense(string id)
    {
        var license = await _licenses.GetByIdAsync(id);
        if (license is null)
            return NotFound(new { success = false, error = "License not found." });

        return Ok(new
        {
            success = true,
            data    = new
            {
                licenseId   = license.LicenseId,
                trackId     = license.TrackId,
                licenseType = license.LicenseType,
                usageType   = license.UsageType,
                buyerId     = license.BuyerId,
                issuedAt    = license.IssuedAt,
                valid        = true,
            },
        });
    }
}
