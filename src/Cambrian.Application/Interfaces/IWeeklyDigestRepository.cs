namespace Cambrian.Application.Interfaces;

/// <summary>A digest candidate before the skip policy is applied.</summary>
public sealed class DigestCandidate
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool EmailVerified { get; set; }
    public bool WeeklyDigestOptOut { get; set; }
    public DateTime? LastWeeklyDigestAtUtc { get; set; }
}

/// <summary>Aggregated weekly numbers for one creator.</summary>
public sealed class DigestWeeklyNumbers
{
    public int Plays { get; set; }
    public int NewFollowers { get; set; }
    public string? TopTrackTitle { get; set; }
    public int TopTrackPlays { get; set; }
}

public interface IWeeklyDigestRepository
{
    /// <summary>Every user who owns at least one public track (digest audience).</summary>
    Task<IReadOnlyList<DigestCandidate>> GetCreatorCandidatesAsync(CancellationToken ct = default);

    /// <summary>Plays / follows / top track within [fromUtc, toUtc) for one creator.</summary>
    Task<DigestWeeklyNumbers> GetWeeklyNumbersAsync(string userId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Stamp the user as digested (drives per-user idempotence).</summary>
    Task MarkDigestSentAsync(string userId, DateTime sentAtUtc, CancellationToken ct = default);

    /// <summary>Set the weekly-digest opt-out flag (unsubscribe endpoint).</summary>
    Task<bool> SetDigestOptOutAsync(string userId, bool optOut, CancellationToken ct = default);
}
