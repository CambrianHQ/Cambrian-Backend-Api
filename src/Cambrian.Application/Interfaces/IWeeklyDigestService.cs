namespace Cambrian.Application.Interfaces;

/// <summary>Per-creator stats aggregated for one digest week.</summary>
public sealed class CreatorDigestStats
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int PlaysThisWeek { get; set; }
    public int NewFollowers { get; set; }
    public string? TopTrackTitle { get; set; }
    public int TopTrackPlays { get; set; }
    public int UnusedReleaseCredits { get; set; }
}

/// <summary>Outcome summary of one digest run (also what the tests assert).</summary>
public sealed class WeeklyDigestRunResult
{
    public bool DryRun { get; set; }
    public DateTime WeekStartUtc { get; set; }
    public int Eligible { get; set; }
    public int Sent { get; set; }
    public int SkippedUnverified { get; set; }
    public int SkippedOptedOut { get; set; }
    public int SkippedAlreadySent { get; set; }
    public int Failed { get; set; }
    public IReadOnlyList<string> Recipients { get; set; } = Array.Empty<string>();
}

public interface IWeeklyDigestService
{
    /// <summary>
    /// Compute per-creator weekly stats and send the digest. Honors the skip
    /// policy (unverified, opted-out, already sent this week). When
    /// <paramref name="dryRun"/> is true, recipients are logged and returned
    /// but nothing is sent and nothing is stamped.
    /// </summary>
    Task<WeeklyDigestRunResult> RunAsync(bool dryRun, CancellationToken ct = default);
}
