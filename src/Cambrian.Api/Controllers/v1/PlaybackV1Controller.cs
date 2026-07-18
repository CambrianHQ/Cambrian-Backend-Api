using System.Diagnostics;
using System.Security.Claims;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Playback;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Cambrian.Api.Controllers.v1;

[ApiController]
[Route("api/v1/tracks")]
[EnableRateLimiting("playback")]
public sealed class PlaybackV1Controller : ControllerBase
{
    private readonly ITrackRepository _tracks;
    private readonly IPlaybackAccessService _access;
    private readonly IPlaybackTicketService _tickets;
    private readonly ILogger<PlaybackV1Controller> _logger;
    private readonly string _backendRelease;

    public PlaybackV1Controller(
        ITrackRepository tracks,
        IPlaybackAccessService access,
        IPlaybackTicketService tickets,
        ILogger<PlaybackV1Controller> logger,
        IOptions<PlaybackMediaOptions> options)
    {
        _tracks = tracks;
        _access = access;
        _tickets = tickets;
        _logger = logger;
        _backendRelease = ResolveBackendRelease(options.Value.BackendRelease);
    }

    [AllowAnonymous]
    [HttpGet("{trackId}/playback")]
    [ProducesResponseType(typeof(PlaybackInfoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PlaybackInfoErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PlaybackInfoErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(PlaybackInfoErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(PlaybackInfoErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPlayback(string trackId, CancellationToken ct)
    {
        ApplyNoStoreHeaders();
        var requestId = HttpContext.Items["RequestId"]?.ToString() ?? HttpContext.TraceIdentifier;
        var traceId = Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier;

        Guid id;
        if (!Guid.TryParse(trackId, out id))
        {
            var resolved = await _tracks.GetByCambrianTrackIdAsync(trackId);
            if (resolved is null)
                return Error(StatusCodes.Status404NotFound, "track_not_found", "Track not found.", requestId, traceId);
            id = resolved.Id;
        }

        var listenerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        PlaybackAccessResult access;
        try
        {
            access = await _access.PrepareAsync(id, listenerId, User.IsInRole("Admin"), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "EVENT: playback_info_failed trackId:{TrackId} requestId:{RequestId}", id, requestId);
            return Error(StatusCodes.Status503ServiceUnavailable,
                "playback_url_generation_failed", "Playback is temporarily unavailable.", requestId, traceId);
        }

        if (access.Outcome != PlaybackAccessOutcome.Ready)
        {
            var status = access.Outcome switch
            {
                PlaybackAccessOutcome.NotFound => StatusCodes.Status404NotFound,
                PlaybackAccessOutcome.Forbidden => StatusCodes.Status403Forbidden,
                PlaybackAccessOutcome.NotReady => StatusCodes.Status409Conflict,
                PlaybackAccessOutcome.ObjectMissing => StatusCodes.Status503ServiceUnavailable,
                PlaybackAccessOutcome.ValidationFailed => StatusCodes.Status422UnprocessableEntity,
                PlaybackAccessOutcome.StorageUnavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status503ServiceUnavailable,
            };
            return Error(status, access.ErrorCode ?? "playback_url_generation_failed",
                access.SafeMessage ?? "Playback is temporarily unavailable.", requestId, traceId);
        }

        PlaybackTicketIssue issued;
        try
        {
            issued = _tickets.Issue(id, access.AuthorizedUserId);
        }
        catch (InvalidOperationException)
        {
            return Error(StatusCodes.Status503ServiceUnavailable,
                "playback_url_generation_failed", "Playback is temporarily unavailable.", requestId, traceId);
        }

        var path = QueryHelpers.AddQueryString($"/stream/{id:D}/audio", "ticket", issued.Ticket);
        var location = $"{Request.Scheme}://{Request.Host}{path}";
        return Ok(new PlaybackInfoResponse(true, new PlaybackInfoData(
            id,
            location,
            issued.ExpiresAtUtc,
            access.MediaState ?? "Ready",
            access.ContentType,
            access.ContentLength,
            requestId,
            traceId,
            _backendRelease)));
    }

    private IActionResult Error(int status, string code, string message, string requestId, string traceId) =>
        StatusCode(status, new PlaybackInfoErrorResponse(false, new PlaybackInfoError(code, message, requestId, traceId)));

    private void ApplyNoStoreHeaders()
    {
        Response.Headers.CacheControl = "private, no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";
        Response.Headers.Vary = "Authorization, Cookie, Origin";
        Response.Headers["X-Backend-Release"] = _backendRelease;
    }

    private static string ResolveBackendRelease(string configured) =>
        !string.IsNullOrWhiteSpace(configured) && configured != "unknown"
            ? configured
            : Environment.GetEnvironmentVariable("GIT_COMMIT")
                ?? Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT")
                ?? "unknown";
}
