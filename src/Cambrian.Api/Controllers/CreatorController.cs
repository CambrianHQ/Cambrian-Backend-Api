using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers;

[Route("creator")]
[Authorize]
[RequireCreatorTier]
[RequireUsername]
[EnableRateLimiting("auth")]
public class CreatorController : BaseController
{
    private readonly ICreatorService _creator;
    private readonly ITrackRepository _tracks;
    private readonly ICreatorIdentityRepository _creators;

    public CreatorController(ICreatorService creator, ITrackRepository tracks, ICreatorIdentityRepository creators)
    {
        _creator = creator;
        _tracks = tracks;
        _creators = creators;
    }

    [HttpGet("tracks")]
    public async Task<IActionResult> Tracks([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;

        var userId = GetRequiredUserId()!;
        var tracks = await _creator.GetTracksAsync(userId, page, pageSize);
        foreach (var t in tracks)
        {
            t.AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio");
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveImageUrl(t.CoverArtUrl);
        }
        return OkResponse(tracks);
    }

    [HttpGet("revenue")]
    public async Task<IActionResult> Revenue()
    {
        var userId = GetRequiredUserId()!;
        var revenue = await _creator.GetRevenueAsync(userId);
        return OkResponse(revenue);
    }

    [HttpPut("tracks/{trackId:guid}")]
    public async Task<IActionResult> EditTrack(Guid trackId, [FromBody] EditTrackRequest request)
    {
        var userId = GetRequiredUserId()!;
        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null) return NotFoundResponse("Track not found.");

        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var ownsLegacy = track.CreatorId == userId;
        var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
        if (!ownsLegacy && !ownsUuid) return ForbiddenResponse("You can only edit your own tracks.");

        if (request.Title is not null) track.Title = request.Title;
        if (request.Description is not null) track.Description = request.Description;
        if (request.Genre is not null) track.Genre = request.Genre;
        if (request.Mood is not null) track.Mood = request.Mood;
        if (request.Tempo is not null) track.Tempo = request.Tempo;
        if (request.Tags is not null) track.Tags = request.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (request.NonExclusivePriceCents.HasValue) track.NonExclusivePriceCents = request.NonExclusivePriceCents.Value;
        if (request.ExclusivePriceCents.HasValue) track.ExclusivePriceCents = request.ExclusivePriceCents.Value;
        if (request.CopyrightBuyoutPriceCents.HasValue) track.CopyrightBuyoutPriceCents = request.CopyrightBuyoutPriceCents.Value;

        await _tracks.UpdateAsync(track);
        return OkResponse(new
        {
            track.Id,
            track.CambrianTrackId,
            track.Title,
            track.Description,
            track.Genre,
            track.Mood,
            track.Tempo,
            track.Tags,
            track.NonExclusivePriceCents,
            track.ExclusivePriceCents,
            track.CopyrightBuyoutPriceCents
        });
    }
}
