namespace Cambrian.Domain.Entities;

public static class TrackMediaStates
{
    public const string Draft = "Draft";
    public const string Uploading = "Uploading";
    public const string Uploaded = "Uploaded";
    public const string Processing = "Processing";
    public const string Validating = "Validating";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
    public const string Quarantined = "Quarantined";
    public const string Deleted = "Deleted";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Draft, Uploading, Uploaded, Processing, Validating, Ready, Failed, Quarantined, Deleted,
    };
}

/// <summary>
/// Media-specific lifecycle for a track. Commerce availability remains on
/// <see cref="Track.Status"/> and must never be inferred from this row.
/// </summary>
public sealed class TrackMedia
{
    public Guid TrackId { get; set; }
    public string? ObjectKey { get; set; }
    public string State { get; set; } = TrackMediaStates.Draft;
    public string? FailureCode { get; set; }
    public string? FailureDetail { get; set; }
    public DateTime StateChangedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ValidatedAtUtc { get; set; }
    public long? SizeBytes { get; set; }
    public string? ContentType { get; set; }
    public string? ChecksumSha256 { get; set; }
    public long? DurationMilliseconds { get; set; }
    public string? ValidationVersion { get; set; }
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();
    public Track Track { get; set; } = null!;
}
