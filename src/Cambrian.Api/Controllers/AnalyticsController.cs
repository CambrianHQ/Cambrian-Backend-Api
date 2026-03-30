using System.Security.Claims;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("analytics")]
public class AnalyticsController : BaseController
{
    private static readonly HashSet<string> AllowedEventTypes = new(StringComparer.Ordinal)
    {
        "play", "download", "purchase", "search", "upload"
    };

    private readonly IAnalyticsRepository _analytics;
    private readonly IAnalyticsService _analyticsService;

    public AnalyticsController(IAnalyticsRepository analytics, IAnalyticsService analyticsService)
    {
        _analytics = analytics;
        _analyticsService = analyticsService;
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

        var eventType = body.EventType.Trim().ToLowerInvariant();
        if (!AllowedEventTypes.Contains(eventType))
            return ErrorResponse($"eventType must be one of: {string.Join(", ", AllowedEventTypes)}");

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
    /// Query analytics events. Admins receive real data; other authenticated users receive an empty list (MVP-safe).
    /// </summary>
    [Authorize]
    [HttpGet("events")]
    public async Task<IActionResult> Events(
        [FromQuery] string? eventType,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = 100)
    {
        // Non-admin users get an empty list — keeps the endpoint accessible without exposing raw event data.
        if (!User.IsInRole("Admin"))
            return OkResponse(new List<object>());

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

    /// <summary>
    /// Record a contract-backed analytics event (track_view, track_click, checkout_started, purchase_completed).
    /// Telemetry only — never blocks checkout or core flows.
    /// </summary>
    [HttpPost("events")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PostEvent([FromBody] AnalyticsEventRequest request, CancellationToken ct)
    {
        try
        {
            await _analyticsService.RecordEventAsync(request, User, ct);
            return Accepted();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
    }
}
