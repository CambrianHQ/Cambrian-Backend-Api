namespace Cambrian.Application.Interfaces;

public enum PlaybackAccessOutcome
{
    Ready,
    NotFound,
    Forbidden,
    NotReady,
    ObjectMissing,
    ValidationFailed,
    StorageUnavailable,
}

public sealed record PlaybackAccessResult(
    PlaybackAccessOutcome Outcome,
    Guid TrackId,
    string? MediaState = null,
    string? ContentType = null,
    long? ContentLength = null,
    string? AuthorizedUserId = null,
    string? ErrorCode = null,
    string? SafeMessage = null,
    string? ObjectKey = null);

public interface IPlaybackAccessService
{
    Task<PlaybackAccessResult> PrepareAsync(
        Guid trackId,
        string? listenerUserId,
        bool isAdmin,
        CancellationToken ct = default);

    Task<PlaybackAccessResult> PrepareTicketStreamAsync(
        Guid trackId,
        bool allowValidating,
        CancellationToken ct = default);

    Task<bool> IsMediaReadyAsync(Guid trackId, CancellationToken ct = default);
}
