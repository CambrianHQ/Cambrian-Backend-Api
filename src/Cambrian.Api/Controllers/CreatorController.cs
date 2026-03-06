using System.Security.Claims;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("creator")]
[Authorize(Roles = "Creator")]
public class CreatorController : BaseController
{
    private readonly ITrackRepository _tracks;
    private readonly IPurchaseRepository _purchases;
    private readonly IPayoutRepository _payouts;

    public CreatorController(ITrackRepository tracks, IPurchaseRepository purchases, IPayoutRepository payouts)
    {
        _tracks = tracks;
        _purchases = purchases;
        _payouts = payouts;
    }

    [HttpGet("tracks")]
    public async Task<IActionResult> Tracks([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var tracks = await _tracks.GetByCreatorIdAsync(userId);

        var paged = tracks
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TrackResponse
            {
                Id = t.Id.ToString(),
                Title = t.Title,
                Genre = t.Genre ?? "",
                Price = (decimal)t.Price
            })
            .ToList();

        return OkResponse(paged);
    }

    [HttpGet("revenue")]
    public async Task<IActionResult> Revenue()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var tracks = await _tracks.GetByCreatorIdAsync(userId);
        var trackIds = tracks.Select(t => t.Id).ToHashSet();

        // Sum completed purchases for creator's tracks
        var allPurchases = new List<Domain.Entities.Purchase>();
        foreach (var trackId in trackIds)
        {
            var tp = await _purchases.GetByTrackIdAsync(trackId);
            allPurchases.AddRange(tp.Where(p => p.Status == "completed"));
        }

        var totalEarned = (decimal)allPurchases.Sum(p => p.Amount);

        // Sum pending payouts
        var payouts = await _payouts.GetByCreatorIdAsync(userId);
        var pendingPayouts = (decimal)payouts.Where(p => p.Status == "pending").Sum(p => p.Amount);
        var paidOut = (decimal)payouts.Where(p => p.Status == "completed").Sum(p => p.Amount);

        return OkResponse(new
        {
            totalEarned,
            pendingBalance = totalEarned - paidOut - pendingPayouts,
            pendingPayouts,
            paidOut
        });
    }
}
