namespace Cambrian.Application.DTOs.Playback;

public sealed record PlaybackInfoData(
    Guid TrackId,
    string Location,
    DateTime ExpiresAtUtc,
    string MediaState,
    string? ContentType,
    long? ContentLength,
    string RequestId,
    string TraceId,
    string BackendRelease);

public sealed record PlaybackInfoResponse(bool Success, PlaybackInfoData Data);

public sealed record PlaybackInfoError(
    string Code,
    string Message,
    string RequestId,
    string TraceId);

public sealed record PlaybackInfoErrorResponse(bool Success, PlaybackInfoError Error);
