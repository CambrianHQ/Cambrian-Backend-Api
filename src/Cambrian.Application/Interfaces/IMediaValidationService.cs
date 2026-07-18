namespace Cambrian.Application.Interfaces;

public sealed record MediaValidationRequest(
    Guid TrackId,
    string ObjectKey,
    long? ExpectedSizeBytes,
    string? ExpectedContentType,
    string? ExpectedChecksumSha256);

public sealed record MediaValidationResult(
    bool IsValid,
    string? FailureCode,
    string? SafeDetail,
    bool DependencyUnavailable,
    long? SizeBytes,
    string? ContentType,
    string? ChecksumSha256,
    long? DurationMilliseconds,
    string ValidationVersion)
{
    public static MediaValidationResult Failure(
        string code,
        string detail,
        string version,
        bool dependencyUnavailable = false,
        long? sizeBytes = null,
        string? contentType = null) =>
        new(false, code, detail, dependencyUnavailable, sizeBytes, contentType, null, null, version);
}

public interface IMediaValidationService
{
    Task<MediaValidationResult> ValidateAsync(MediaValidationRequest request, CancellationToken ct = default);
}
