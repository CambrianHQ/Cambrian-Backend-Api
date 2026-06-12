using System.Text.Json;
using Cambrian.Application.DTOs.Readiness;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

/// <summary>
/// Weighted release-readiness scoring. Unlike the equal-weight
/// <see cref="ComplianceScoreService"/>, this is the distributor-facing
/// pre-release gate: loudness 25, metadata 25, AI disclosure 25, cover 15,
/// provenance 10; pass = full weight, warn = half, fail = 0.
/// </summary>
public sealed class TrackReadinessService : ITrackReadinessService
{
    private const double LoudnessTarget = -14.0;
    private const double LoudnessPassTolerance = 1.0;
    private const double LoudnessWarnTolerance = 2.0;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ITrackRepository _tracks;
    private readonly IMasteringJobRepository _jobs;
    private readonly ITrackAuthorshipRepository _authorship;
    private readonly IObjectStorage _storage;
    private readonly IReleaseValidationService _validation;
    private readonly ITrackReadinessCache _cache;
    private readonly ILogger<TrackReadinessService> _logger;

    public TrackReadinessService(
        ITrackRepository tracks,
        IMasteringJobRepository jobs,
        ITrackAuthorshipRepository authorship,
        IObjectStorage storage,
        IReleaseValidationService validation,
        ITrackReadinessCache cache,
        ILogger<TrackReadinessService> logger)
    {
        _tracks = tracks;
        _jobs = jobs;
        _authorship = authorship;
        _storage = storage;
        _validation = validation;
        _cache = cache;
        _logger = logger;
    }

    public async Task<TrackReadinessResponse?> GetAsync(Guid trackId, CancellationToken ct = default)
    {
        if (_cache.Get(trackId) is { } cached)
            return cached;

        var track = await _tracks.GetByIdAsync(trackId);
        if (track is null)
            return null;

        var latestJob = await _jobs.GetLatestForTrackAsync(trackId, ct);
        var authorship = await _authorship.GetByTrackIdAsync(trackId, ct);

        var checks = new List<(ReadinessCheck Check, int Weight)>
        {
            (Loudness(latestJob), 25),
            (Metadata(track), 25),
            (AiDisclosure(track, authorship), 25),
            (await CoverAsync(track, latestJob), 15),
            (Provenance(track), 10),
        };

        var score = checks.Sum(c => c.Check.Status switch
        {
            "pass" => c.Weight,
            "warn" => c.Weight / 2,
            _ => 0,
        });

        var response = new TrackReadinessResponse
        {
            Score = score,
            Checks = checks.Select(c => c.Check).ToList(),
        };

        _cache.Set(trackId, response);
        return response;
    }

    // ── Loudness (25): measured integrated LUFS from the latest mastering job ──
    private static ReadinessCheck Loudness(MasteringJob? job)
    {
        // Prefer the mastered output measurement; an un-mastered upload's input
        // measurement still counts (the source may already be at target).
        var measured = job?.OutputLufs ?? job?.InputLufs;
        if (measured is not double lufs)
            return Check("loudness", "fail",
                "No loudness measurement yet — run Release Ready mastering to measure and normalize to −14 LUFS.");

        var deviation = Math.Abs(lufs - LoudnessTarget);
        if (deviation <= LoudnessPassTolerance)
            return Check("loudness", "pass", $"Integrated loudness is {lufs:0.0} LUFS (target −14 ±1).");
        if (deviation <= LoudnessWarnTolerance)
            return Check("loudness", "warn", $"Integrated loudness is {lufs:0.0} LUFS — slightly outside the −14 ±1 window.");
        return Check("loudness", "fail", $"Integrated loudness is {lufs:0.0} LUFS — outside the −14 ±1 release window.");
    }

    // ── Metadata completeness (25) ──
    private static ReadinessCheck Metadata(Track track)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(track.PrimaryGenre) &&
            string.IsNullOrWhiteSpace(track.Genre) &&
            string.IsNullOrWhiteSpace(track.Subgenre)) missing.Add("genre");
        if (string.IsNullOrWhiteSpace(track.Description)) missing.Add("description");
        if (string.IsNullOrWhiteSpace(track.Mood)) missing.Add("mood");
        if (string.IsNullOrWhiteSpace(track.Tempo)) missing.Add("tempo");
        if (string.IsNullOrWhiteSpace(track.Title)) missing.Add("title");

        if (missing.Count == 0)
            return Check("metadata", "pass", "All required metadata fields are filled in.");
        if (missing.Count <= 2)
            return Check("metadata", "warn", $"Metadata is partially complete — missing: {string.Join(", ", missing)}.");
        return Check("metadata", "fail", $"Most metadata is missing: {string.Join(", ", missing)}.");
    }

    // ── DDEX AI disclosure (25) ──
    private static ReadinessCheck AiDisclosure(Track track, TrackAuthorship? authorship)
    {
        var present = !string.IsNullOrWhiteSpace(track.AiDisclosureDdex)
                      || !string.IsNullOrWhiteSpace(authorship?.AiDisclosure);
        if (present)
            return Check("aiDisclosure", "pass", "DDEX-aligned AI-use disclosure is on file.");
        return Check("aiDisclosure", "fail",
            "No AI-use disclosure on file. Distributors require disclosure even for fully human works (\"none\" is a valid disclosure).");
    }

    // ── Cover spec: 3000×3000 JPEG/PNG (15) ──
    private async Task<ReadinessCheck> CoverAsync(Track track, MasteringJob? job)
    {
        // A validated artwork result from the latest Release Ready job is authoritative.
        var reportArtwork = ParseArtwork(job?.ValidationReportJson);
        if (reportArtwork is { Provided: true })
        {
            return reportArtwork.Passed
                ? Check("cover", "pass", $"Cover art validated at {reportArtwork.Width}×{reportArtwork.Height} {reportArtwork.Format}.")
                : Check("cover", "fail", $"Cover art rejected: {string.Join(" ", reportArtwork.Issues)}");
        }

        if (string.IsNullOrWhiteSpace(track.CoverArtUrl))
            return Check("cover", "fail", "No cover art. Add a 3000×3000 JPEG or PNG.");

        // Validate the stored cover directly (cached, so this cost is rarely paid).
        try
        {
            using var file = await _storage.OpenReadAsync(track.CoverArtUrl!);
            if (file is not null)
            {
                var result = _validation.ValidateArtwork(file.Stream, track.CoverArtUrl);
                if (result.Passed)
                    return Check("cover", "pass", $"Cover art validated at {result.Width}×{result.Height} {result.Format}.");
                if (result.Width is not null)
                    return Check("cover", "fail", $"Cover art does not meet spec: {string.Join(" ", result.Issues)}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EVENT: ReadinessCoverProbeFailed trackId:{TrackId}", track.Id);
        }

        return Check("cover", "warn", "Cover art is present but could not be verified against the 3000×3000 JPEG/PNG spec.");
    }

    // ── Provenance stamp (10) ──
    private static ReadinessCheck Provenance(Track track)
    {
        if (!string.IsNullOrWhiteSpace(track.Signature))
            return Check("provenance", "pass", "A signed provenance stamp exists for this track.");
        if (!string.IsNullOrWhiteSpace(track.ContentHash))
            return Check("provenance", "warn", "The audio is hashed but no provenance stamp has been issued yet.");
        return Check("provenance", "fail", "No provenance stamp — the track has not been hashed or signed.");
    }

    private static ArtworkValidationResult? ParseArtwork(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ValidationReport>(reportJson, JsonOpts)?.Artwork;
        }
        catch (JsonException)
        {
            return null; // tolerate legacy/garbled rows
        }
    }

    private static ReadinessCheck Check(string key, string status, string detail) =>
        new() { Key = key, Status = status, Detail = detail };
}
