using Cambrian.Application.DTOs.Provenance;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <inheritdoc cref="IAuthorshipService" />
public sealed class AuthorshipService : IAuthorshipService
{
    private readonly ITrackAuthorshipRepository _authorship;
    private readonly ITrackRepository _tracks;
    private readonly ITrackReadinessCache _readinessCache;
    private readonly ILogger<AuthorshipService> _logger;

    public AuthorshipService(
        ITrackAuthorshipRepository authorship,
        ITrackRepository tracks,
        ITrackReadinessCache readinessCache,
        ILogger<AuthorshipService> logger)
    {
        _authorship = authorship;
        _tracks = tracks;
        _readinessCache = readinessCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TrackAuthorshipResponse?> GetAsync(Track track, CancellationToken ct = default)
    {
        var row = await _authorship.GetByTrackIdAsync(track.Id, ct);
        return row is null ? null : Map(row, track.CommercialRightsVerified);
    }

    /// <inheritdoc />
    public async Task<TrackAuthorshipResponse> UpsertAsync(Track track, TrackAuthorshipRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var row = await _authorship.GetByTrackIdAsync(track.Id, ct);

        if (row is null)
        {
            row = new TrackAuthorship { Id = Guid.NewGuid(), TrackId = track.Id, CreatedAt = now };
            Apply(row, request, now);
            await _authorship.AddAsync(row, ct);
        }
        else
        {
            Apply(row, request, now);
            await _authorship.UpdateAsync(row, ct);
        }

        // Track-level commercial-rights attestation travels with the authorship upsert
        // (batch-1 placeholder for the §9.5 verification flow). Null means "not part of
        // this save" (e.g. a frontend screen editing only the AI disclosure section) and
        // must leave the existing attestation alone rather than resetting it to false.
        if (request.CommercialRightsVerified.HasValue
            && track.CommercialRightsVerified != request.CommercialRightsVerified.Value)
        {
            track.CommercialRightsVerified = request.CommercialRightsVerified.Value;
            await _tracks.UpdateAsync(track);
        }

        _readinessCache.Invalidate(track.Id);

        _logger.LogInformation(
            "EVENT: AuthorshipUpserted trackId:{TrackId} rightsVerified:{Rights}",
            track.Id, track.CommercialRightsVerified);

        return Map(row, track.CommercialRightsVerified);
    }

    /// <summary>
    /// Partial update — a null field on <paramref name="req"/> means "not part of this
    /// save" and leaves the existing stored value untouched. Send an explicit empty
    /// string to intentionally clear a narrative field.
    /// </summary>
    private static void Apply(TrackAuthorship row, TrackAuthorshipRequest req, DateTime now)
    {
        if (req.Edits is not null) row.Edits = Normalize(req.Edits);
        if (req.ArrangementNotes is not null) row.ArrangementNotes = Normalize(req.ArrangementNotes);
        if (req.LyricsAuthored.HasValue) row.LyricsAuthored = req.LyricsAuthored.Value;
        if (req.ProcessNotes is not null) row.ProcessNotes = Normalize(req.ProcessNotes);
        if (req.AiDisclosure is not null) row.AiDisclosure = Normalize(req.AiDisclosure);
        row.UpdatedAt = now;
    }

    private static TrackAuthorshipResponse Map(TrackAuthorship row, bool commercialRightsVerified) => new()
    {
        Edits = row.Edits,
        ArrangementNotes = row.ArrangementNotes,
        LyricsAuthored = row.LyricsAuthored,
        ProcessNotes = row.ProcessNotes,
        AiDisclosure = row.AiDisclosure,
        CommercialRightsVerified = commercialRightsVerified,
        UpdatedAt = row.UpdatedAt,
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
