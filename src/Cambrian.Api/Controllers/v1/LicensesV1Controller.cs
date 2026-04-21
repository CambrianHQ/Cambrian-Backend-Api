using System.Security.Claims;
using System.Text.Json;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Checkout;
using Cambrian.Application.DTOs.V1;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Interfaces.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers.v1;

/// <summary>
/// Public versioned API — license purchase initiation and verification.
/// Purchase requires authentication (JWT or API key); verification is public.
/// Rate-limited per key/IP via the "api_key_free" policy. Every call is
/// recorded as an "api_call" analytics event by <see cref="ApiUsageActionFilter"/>.
/// </summary>
[ApiController]
[Route("api/v1")]
[EnableRateLimiting("api_key_free")]
[ServiceFilter(typeof(ApiUsageActionFilter))]
public class LicensesV1Controller : ControllerBase
{
    private const string PurchaseRouteKey = "POST /api/v1/licenses/purchase";

    private readonly ICheckoutService _checkout;
    private readonly ILicenseService _licenses;
    private readonly IIdempotencyStore _idempotency;
    private readonly ILogger<LicensesV1Controller> _logger;

    public LicensesV1Controller(
        ICheckoutService checkout,
        ILicenseService licenses,
        IIdempotencyStore idempotency,
        ILogger<LicensesV1Controller> logger)
    {
        _checkout = checkout;
        _licenses = licenses;
        _idempotency = idempotency;
        _logger = logger;
    }

    /// <summary>
    /// Initiate a Stripe checkout session for a track license.
    /// Canonical route. <c>POST /api/v1/licenses</c> is kept as a backward-compatible alias.
    ///
    /// Idempotent when the client supplies an <c>Idempotency-Key</c> header
    /// (UUID recommended). Subsequent calls with the same key from the same
    /// authenticated user return the original response without contacting
    /// Stripe again. TTL: 24 hours.
    /// </summary>
    [HttpPost("licenses/purchase")]
    [HttpPost("licenses")]
    [Authorize]
    public async Task<ActionResult<V1ApiResponse<LicensePurchaseResponse>>> Purchase(
        [FromBody] LicensePurchaseRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(V1ApiResponse<LicensePurchaseResponse>.Fail("Authentication required."));

        // ── Idempotency replay check ──
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var cached = await _idempotency.TryGetAsync(idempotencyKey, userId, PurchaseRouteKey, ct);
            if (cached is not null)
            {
                _logger.LogInformation("EVENT: V1PurchaseIdempotentReplay userId:{UserId} key:{Key}", userId, idempotencyKey);
                // Re-emit the cached body wrapped in our envelope, mark as idempotent replay.
                var cachedResponse = JsonSerializer.Deserialize<LicensePurchaseResponse>(cached.Body)
                                     ?? throw new InvalidOperationException("Idempotency cache corrupt.");
                cachedResponse = cachedResponse with { Idempotent = true };
                return StatusCode(cached.StatusCode, V1ApiResponse<LicensePurchaseResponse>.Ok(cachedResponse));
            }
        }

        // ── Delegate to Application-layer service (no business logic in controller) ──
        var checkoutRequest = new CheckoutRequest
        {
            TrackId = request.TrackId,
            LicenseType = request.LicenseType,
            UsageType = request.UsageType ?? "personal",
            ClientReferenceId = request.ClientReferenceId,
        };

        try
        {
            var session = await _checkout.CreateCheckoutAsync(checkoutRequest, User);

            var response = new LicensePurchaseResponse
            {
                CheckoutUrl = session.CheckoutUrl,
                Status = session.Status,
                Idempotent = false,
            };

            // Persist for replay protection (only successful results).
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var body = JsonSerializer.Serialize(response);
                await _idempotency.SaveAsync(idempotencyKey, userId, PurchaseRouteKey, StatusCodes.Status200OK, body, ct: ct);
            }

            return Ok(V1ApiResponse<LicensePurchaseResponse>.Ok(response));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(V1ApiResponse<LicensePurchaseResponse>.Fail(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, V1ApiResponse<LicensePurchaseResponse>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Verify a license certificate. Public — no authentication required.
    /// Use this to confirm that a buyer owns a valid license for a track.
    /// </summary>
    [HttpGet("licenses/{id}/verify")]
    [AllowAnonymous]
    public async Task<ActionResult<V1ApiResponse<LicenseVerifyResponse>>> VerifyLicense(string id)
    {
        var license = await _licenses.GetByIdAsync(id);
        if (license is null)
            return NotFound(V1ApiResponse<LicenseVerifyResponse>.Fail("License not found."));

        return Ok(V1ApiResponse<LicenseVerifyResponse>.Ok(new LicenseVerifyResponse
        {
            LicenseId = license.LicenseId,
            TrackId = license.TrackId,
            LicenseType = license.LicenseType,
            UsageType = license.UsageType,
            BuyerId = license.BuyerId,
            IssuedAt = license.IssuedAt,
            Valid = true,
        }));
    }
}
