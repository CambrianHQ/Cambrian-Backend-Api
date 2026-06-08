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
    private readonly ILogger<AuthorshipService> _logger;

    public AuthorshipService(
        ITrackAuthorshipRepository authorship,
        ITrackRepository tracks,
        ILogger<AuthorshipService> logger)
    {
        _authorship = authorship;
        _tracks = tracks;
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
        // (batch-1 placeholder for the §9.5 verification flow). Only write when it changes.
        if (track.CommercialRightsVerified != request.CommercialRightsVerified)
        {
            track.CommercialRightsVerified = request.CommercialRightsVerified;
            await _tracks.UpdateAsync(track);
        }

        _logger.LogInformation(
            "EVENT: AuthorshipUpserted trackId:{TrackId} rightsVerified:{Rights}",
            track.Id, request.CommercialRightsVerified);

        return Map(row, request.CommercialRightsVerified);
    }

    private static void Apply(TrackAuthorship row, TrackAuthorshipRequest req, DateTime now)
    {
        row.Edits = Normalize(req.Edits);
        row.ArrangementNotes = Normalize(req.ArrangementNotes);
        row.LyricsAuthored = req.LyricsAuthored;
        row.ProcessNotes = Normalize(req.ProcessNotes);
        row.AiDisclosure = Normalize(req.AiDisclosure);
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
