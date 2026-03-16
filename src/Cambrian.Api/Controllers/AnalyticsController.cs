using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("analytics")]
public class AnalyticsController : BaseController
{
    private readonly IAnalyticsRepository _analytics;

    public AnalyticsController(IAnalyticsRepository analytics)
    {
        _analytics = analytics;
    }

    /// <summary>
    /// Record a usage event (play, download, purchase, search, upload).
    /// </summary>
    [Authorize]
    [HttpPost("track")]
    public async Task<IActionResult> RecordEvent([FromBody] RecordEventRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.EventType))
            return ErrorResponse("eventType is required.");

        var allowed = new[] { "play", "download", "purchase", "search", "upload" };
        var eventType = body.EventType.Trim().ToLowerInvariant();
        var matched = false;
        foreach (var a in allowed)
        {
            if (a == eventType) { matched = true; break; }
        }
        if (!matched)
            return ErrorResponse($"eventType must be one of: {string.Join(", ", allowed)}");

        Guid? trackId = null;
        if (!string.IsNullOrWhiteSpace(body.TrackId) && Guid.TryParse(body.TrackId, out var parsed))
            trackId = parsed;

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        await _analytics.RecordAsync(eventType, userId, trackId, body.Metadata);

        return MessageResponse("Event recorded.");
    }

    /// <summary>
    /// Admin: get event counts grouped by type.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var counts = await _analytics.GetCountsByTypeAsync(from, to);
        return OkResponse(counts);
    }

    /// <summary>
    /// Admin: query raw analytics events.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpGet("events")]
    public async Task<IActionResult> Events(
        [FromQuery] string? eventType,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = 100)
    {
        var events = await _analytics.QueryAsync(eventType, from, to, limit);
        var result = new List<object>();
        foreach (var e in events)
        {
            result.Add(new
            {
                id = e.Id.ToString(),
                eventType = e.EventType,
                userId = e.UserId,
                trackId = e.TrackId?.ToString(),
                metadata = e.Metadata,
                createdAt = e.CreatedAt,
            });
        }
        return OkResponse(result);
    }

    public class RecordEventRequest
    {
        public string EventType { get; set; } = "";
        public string? TrackId { get; set; }
        public string? Metadata { get; set; }
    }
}
