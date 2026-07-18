namespace Cambrian.Application.Interfaces;

public sealed record MediaReadinessResult(
    bool IsReady,
    string? FailureCode = null,
    string? SafeMessage = null,
    string? MediaState = null);

public interface IMediaReadinessService
{
    /// <summary>
    /// Ensures a track's media is Ready before publication, synchronously validating
    /// and promoting promotable states (Uploaded/Failed) through the media state
    /// machine. Never publishes unverifiable media: absent, quarantined, or
    /// validation-failed media returns a not-ready result with a safe failure code.
    /// </summary>
    Task<MediaReadinessResult> EnsureReadyAsync(Guid trackId, CancellationToken ct = default);
}
