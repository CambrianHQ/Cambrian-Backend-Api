namespace Cambrian.Application.Interfaces;

public sealed record MediaReconciliationSummary(
    Guid RunId,
    string Status,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    int TracksInspected,
    int ObjectsInspected,
    int FindingCount,
    int UnresolvedPublishedTrackFailures);

public sealed record MediaReconciliationFindingDto(
    Guid Id,
    Guid? TrackId,
    string FindingType,
    string Severity,
    string Detail,
    string Resolution,
    DateTime CreatedAtUtc);

public sealed record MediaReconciliationReport(
    MediaReconciliationSummary Run,
    IReadOnlyList<MediaReconciliationFindingDto> Findings);

public interface IMediaReconciliationService
{
    Task<MediaReconciliationSummary> RunAsync(bool remediate, CancellationToken ct = default);
    Task<MediaReconciliationSummary> CreateRunAsync(bool remediate, CancellationToken ct = default);
    Task<MediaReconciliationSummary> ExecuteRunAsync(Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<MediaReconciliationSummary>> GetRunsAsync(int take, CancellationToken ct = default);
    Task<MediaReconciliationReport?> GetRunAsync(Guid runId, CancellationToken ct = default);
}

/// <summary>
/// Process-wide single-run guard shared by the admin trigger and the in-process
/// worker so reconciliation runs can never overlap within one host.
/// </summary>
public static class MediaReconciliationRunGuard
{
    private static int _active;

    public static bool TryAcquire() => Interlocked.CompareExchange(ref _active, 1, 0) == 0;

    public static void Release() => Interlocked.Exchange(ref _active, 0);
}
