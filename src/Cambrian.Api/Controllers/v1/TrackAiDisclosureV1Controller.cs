using System.Security.Claims;
using Cambrian.Api.Middleware;
using Cambrian.Api.Security;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.V1;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers.v1;

/// <summary>Versioned, structured AI disclosure contract based on the July 10, 2026 definitions.</summary>
[ApiController]
[Route("api/v1/tracks/{trackId}/ai-disclosure")]
[EnableRateLimiting("api_key_free")]
[ServiceFilter(typeof(ApiUsageActionFilter))]
public sealed class TrackAiDisclosureV1Controller : ControllerBase
{
    private readonly ITrackRepository _tracks;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ITrackVisibilityPolicy _visibility;
    private readonly ITrackAiDisclosureRepository _disclosures;

    public TrackAiDisclosureV1Controller(ITrackRepository tracks, ICreatorIdentityRepository creators,
        ITrackVisibilityPolicy visibility, ITrackAiDisclosureRepository disclosures)
    {
        _tracks = tracks; _creators = creators; _visibility = visibility; _disclosures = disclosures;
    }

    /// <summary>Returns Unclassified/version 0 when the creator has never submitted a disclosure.</summary>
    [HttpGet]
    [AllowAnonymous]
    [AllowApiKey]
    [ProducesResponseType(typeof(V1ApiResponse<PublicTrackAiDisclosureDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string trackId)
    {
        var track = await ResolveTrackAsync(trackId);
        if (track is null || !CanView(track)) return NotFound(V1ApiResponse<object>.Fail("Track not found."));
        return Ok(V1ApiResponse<PublicTrackAiDisclosureDto>.Ok(await _disclosures.GetPublicAsync(track.Id)));
    }

    /// <summary>Create the first owner-authored disclosure. Legacy tracks are never auto-created.</summary>
    [HttpPost]
    [Authorize(Policy = "CanEditOwnTrack")]
    [ProducesResponseType(typeof(V1ApiResponse<PublicTrackAiDisclosureDto>), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(string trackId, [FromBody] UpsertTrackAiDisclosureRequest request)
    {
        var ownership = await ResolveOwnedTrackAsync(trackId);
        if (ownership.Error is not null) return ownership.Error;
        if (!ValidateRequest(request, out var error)) return BadRequest(V1ApiResponse<object>.Fail(error!));
        var result = await _disclosures.CreateAsync(ownership.Track!.Id, ownership.UserId!, request);
        return result.Status == DisclosureWriteStatus.AlreadyExists
            ? Conflict(V1ApiResponse<object>.Fail("An AI disclosure already exists; use PUT to update it."))
            : StatusCode(StatusCodes.Status201Created, V1ApiResponse<PublicTrackAiDisclosureDto>.Ok(result.Disclosure!));
    }

    /// <summary>Update or correct a disclosure. Supply expectedVersion for optimistic concurrency protection.</summary>
    [HttpPut]
    [Authorize(Policy = "CanEditOwnTrack")]
    [ProducesResponseType(typeof(V1ApiResponse<PublicTrackAiDisclosureDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(string trackId, [FromBody] UpsertTrackAiDisclosureRequest request)
    {
        var ownership = await ResolveOwnedTrackAsync(trackId);
        if (ownership.Error is not null) return ownership.Error;
        if (!ValidateRequest(request, out var error)) return BadRequest(V1ApiResponse<object>.Fail(error!));
        var result = await _disclosures.UpdateAsync(ownership.Track!.Id, ownership.UserId!, request);
        return WriteResult(result, "No AI disclosure exists; use POST to create it.");
    }

    /// <summary>Revoke the current disclosure, returning its public state to Unclassified while retaining audit history.</summary>
    [HttpPost("revoke")]
    [Authorize(Policy = "CanEditOwnTrack")]
    public async Task<IActionResult> Revoke(string trackId, [FromBody] RevokeTrackAiDisclosureRequest request)
    {
        var ownership = await ResolveOwnedTrackAsync(trackId);
        if (ownership.Error is not null) return ownership.Error;
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(V1ApiResponse<object>.Fail("A revocation reason is required."));
        var result = await _disclosures.RevokeAsync(ownership.Track!.Id, ownership.UserId!, request);
        return WriteResult(result, "No AI disclosure exists to revoke.");
    }

    /// <summary>Owner-only immutable audit history. Prior disclosures are not exposed publicly.</summary>
    [HttpGet("history")]
    [Authorize(Policy = "CanEditOwnTrack")]
    public async Task<IActionResult> History(string trackId)
    {
        var ownership = await ResolveOwnedTrackAsync(trackId);
        if (ownership.Error is not null) return ownership.Error;
        var history = await _disclosures.GetHistoryAsync(ownership.Track!.Id);
        return Ok(V1ApiResponse<IReadOnlyList<TrackAiDisclosureRevisionDto>>.Ok(history));
    }

    private IActionResult WriteResult(DisclosureWriteResult result, string missingMessage) => result.Status switch
    {
        DisclosureWriteStatus.Success => Ok(V1ApiResponse<PublicTrackAiDisclosureDto>.Ok(result.Disclosure!)),
        DisclosureWriteStatus.NotFound => NotFound(V1ApiResponse<object>.Fail(missingMessage)),
        DisclosureWriteStatus.VersionConflict => Conflict(V1ApiResponse<object>.Fail("The disclosure changed since it was read. Fetch the latest version and retry.")),
        _ => Conflict(V1ApiResponse<object>.Fail("The disclosure could not be changed.")),
    };

    private static bool ValidateRequest(UpsertTrackAiDisclosureRequest request, out string? error)
    {
        error = null;
        if (!Enum.IsDefined(request.Classification) || request.Classification == Cambrian.Domain.Entities.AiTrackClassification.Unclassified)
            error = "Classification must be AIGenerated or AIAssisted. Use the revoke endpoint to return to Unclassified.";
        else if (request.CreationDate > DateOnly.FromDateTime(DateTime.UtcNow))
            error = "CreationDate cannot be in the future.";
        else if (request.ExpectedVersion is <= 0)
            error = "ExpectedVersion must be greater than zero when supplied.";
        else if (request.Collaborators?.Any(x => string.IsNullOrWhiteSpace(x) || x.Trim().Length > 200) == true)
            error = "Each collaborator must be between 1 and 200 characters.";
        else if (request.Collaborators?.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 50)
            error = "No more than 50 collaborators may be supplied.";
        return error is null;
    }

    private bool CanView(Cambrian.Domain.Entities.Track track)
    {
        var userId = GetUserId();
        return _visibility.CanAccess(track.Visibility ?? "public", track.CreatorId, userId, User.IsInRole("Admin"));
    }

    private async Task<(Cambrian.Domain.Entities.Track? Track, string? UserId, IActionResult? Error)> ResolveOwnedTrackAsync(string trackId)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId)) return (null, null, Unauthorized(V1ApiResponse<object>.Fail("Authentication required.")));
        var track = await ResolveTrackAsync(trackId);
        if (track is null) return (null, userId, NotFound(V1ApiResponse<object>.Fail("Track not found.")));
        if (track.CreatorId == userId) return (track, userId, null);
        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        return creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value
            ? (track, userId, null)
            : (track, userId, StatusCode(403, V1ApiResponse<object>.Fail("You can only edit your own tracks.")));
    }

    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);

    private async Task<Cambrian.Domain.Entities.Track?> ResolveTrackAsync(string trackId) => Guid.TryParse(trackId, out var id)
        ? await _tracks.GetByIdAsync(id)
        : await _tracks.GetByCambrianTrackIdAsync(trackId);
}
