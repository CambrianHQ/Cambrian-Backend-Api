using System.Security.Claims;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Persistence.Services;

public sealed class AnalyticsService : IAnalyticsService
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "track_view",
        "track_click",
        "checkout_started",
        "purchase_completed"
    };

    private readonly CambrianDbContext _db;
    private readonly IFeatureFlagService _flags;

    public AnalyticsService(CambrianDbContext db, IFeatureFlagService flags)
    {
        _db = db;
        _flags = flags;
    }

    public async Task RecordEventAsync(AnalyticsEventRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await _flags.IsEnabledAsync("AnalyticsCaptureEnabled", ct))
            return;

        if (string.IsNullOrWhiteSpace(request.Type) || !AllowedTypes.Contains(request.Type))
            throw new ArgumentException("Unsupported analytics event type.");

        var row = new AnalyticsEvent
        {
            Id = Guid.NewGuid(),
            UserId = TryGetUserId(user),
            TrackId = request.TrackId,
            EventType = request.Type,
            Metadata = request.MetadataJson,
            IsSimulated = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.AnalyticsEvents.Add(row);
        await _db.SaveChangesAsync(ct);
    }

    private static string? TryGetUserId(ClaimsPrincipal user)
    {
        return user.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
