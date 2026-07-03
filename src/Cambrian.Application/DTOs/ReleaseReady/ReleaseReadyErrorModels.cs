namespace Cambrian.Application.DTOs.ReleaseReady;

public static class ReleaseReadyErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string AudioTooShort = "audio_too_short";
    public const string AudioTooLong = "audio_too_long";
    public const string InvalidAudio = "invalid_audio";
    public const string MissingCoverArt = "missing_cover_art";
    public const string MissingMetadata = "missing_metadata";
    public const string InsufficientCredits = "insufficient_credits";
    public const string DuplicateSubmission = "duplicate_submission";
    public const string EmailNotVerified = "email_not_verified";
    public const string StorageError = "storage_error";
}

public sealed class ReleaseReadyErrorResponse
{
    public bool Success { get; init; } = false;
    public ReleaseReadyError Error { get; init; } = new();
    public IReadOnlyList<ReleaseReadyError> Errors { get; init; } = Array.Empty<ReleaseReadyError>();
    public ValidationReport? Validation { get; init; }
}

public sealed class ReleaseReadyError
{
    public string Code { get; init; } = ReleaseReadyErrorCodes.ValidationFailed;
    public string Message { get; init; } = "Release Ready request failed.";
    public string? Field { get; init; }
    public object? Details { get; init; }
}
