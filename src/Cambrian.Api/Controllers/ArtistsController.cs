using Cambrian.Application.DTOs.Monetization;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Artist money-in (contract: docs/api-contracts.md): tips and fan subscriptions
/// on the artist's Stripe connected account. 409 when the artist hasn't enabled
/// payouts.
/// </summary>
[Route("api/artists")]
[Authorize]
public sealed class ArtistsController : BaseController
{
    private readonly IArtistMonetizationService _monetization;

    public ArtistsController(IArtistMonetizationService monetization)
    {
        _monetization = monetization;
    }

    /// <summary>Create a tip checkout session on the artist's connected account (no platform fee at launch).</summary>
    [HttpPost("{id}/tip")]
    public async Task<IActionResult> Tip(string id, [FromBody] TipRequest request, CancellationToken ct)
    {
        var payerUserId = GetRequiredUserId()!;
        try
        {
            var result = await _monetization.CreateTipCheckoutAsync(id, request?.AmountCents ?? 0, payerUserId, ct);
            return OkResponse(result);
        }
        catch (KeyNotFoundException) { return NotFoundResponse("Artist not found."); }
        catch (ArtistPayoutsNotEnabledException ex) { return ConflictResponse(ex.Message); }
        catch (ArgumentException ex) { return ErrorResponse(ex.Message); }
    }

    /// <summary>Create a monthly fan-subscription checkout at the artist-set price (15% platform fee).</summary>
    [HttpPost("{id}/subscribe")]
    public async Task<IActionResult> Subscribe(string id, CancellationToken ct)
    {
        var payerUserId = GetRequiredUserId()!;
        try
        {
            var result = await _monetization.CreateFanSubscriptionCheckoutAsync(id, payerUserId, ct);
            return OkResponse(result);
        }
        catch (KeyNotFoundException) { return NotFoundResponse("Artist not found."); }
        catch (ArtistPayoutsNotEnabledException ex) { return ConflictResponse(ex.Message); }
        catch (InvalidOperationException ex) { return ConflictResponse(ex.Message); }
        catch (ArgumentException ex) { return ErrorResponse(ex.Message); }
    }

    /// <summary>
    /// Creator dashboard: the caller's own money-in summary (tips + fan subscriptions).
    /// Strictly scoped to the authenticated user — never another creator's earnings.
    /// </summary>
    [HttpGet("me/support")]
    public async Task<IActionResult> Support(CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        var summary = await _monetization.GetSupportSummaryAsync(userId, ct);
        return OkResponse(summary);
    }

    /// <summary>Set (or clear) the caller's monthly fan-subscription price.</summary>
    [HttpPut("me/subscription-price")]
    public async Task<IActionResult> SetSubscriptionPrice([FromBody] SetSubscriptionPriceRequest request, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        try
        {
            await _monetization.SetSubscriptionPriceAsync(userId, request?.PriceCents, ct);
            return MessageResponse("Subscription price updated.");
        }
        catch (ArgumentException ex) { return ErrorResponse(ex.Message); }
    }
}
