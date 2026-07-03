using Cambrian.Application.DTOs.ReleaseReady;

namespace Cambrian.Application.Exceptions;

public sealed class ReleaseReadyValidationException : InvalidOperationException
{
    public ReleaseReadyValidationException(string message, ValidationReport validation)
        : base(message)
    {
        Validation = validation;
        Errors = BuildErrors(validation);
    }

    public ValidationReport Validation { get; }
    public IReadOnlyList<ReleaseReadyError> Errors { get; }

    private static IReadOnlyList<ReleaseReadyError> BuildErrors(ValidationReport validation)
    {
        var errors = new List<ReleaseReadyError>();
        errors.AddRange(validation.Metadata.Issues.Select(MapMetadataIssue));
        errors.AddRange(validation.Artwork.Issues.Select(issue => MapArtworkIssue(issue, validation.Artwork.Provided)));
        return errors.Count == 0
            ? new[] { new ReleaseReadyError { Code = ReleaseReadyErrorCodes.ValidationFailed, Message = "Release Ready validation failed." } }
            : errors;
    }

    private static ReleaseReadyError MapMetadataIssue(string issue)
    {
        var code = issue switch
        {
            var s when s.StartsWith("Audio must be at least", StringComparison.OrdinalIgnoreCase)
                => ReleaseReadyErrorCodes.AudioTooShort,
            var s when s.StartsWith("Release Ready currently supports tracks up to", StringComparison.OrdinalIgnoreCase)
                => ReleaseReadyErrorCodes.AudioTooLong,
            var s when s.StartsWith("Could not read audio metadata", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("Could not determine audio duration", StringComparison.OrdinalIgnoreCase)
                => ReleaseReadyErrorCodes.InvalidAudio,
            var s when s.StartsWith("Missing", StringComparison.OrdinalIgnoreCase)
                || s.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
                || s.Contains("AI tool name", StringComparison.OrdinalIgnoreCase)
                => ReleaseReadyErrorCodes.MissingMetadata,
            _ => ReleaseReadyErrorCodes.ValidationFailed,
        };

        return new ReleaseReadyError
        {
            Code = code,
            Message = issue,
            Field = code is ReleaseReadyErrorCodes.InvalidAudio
                or ReleaseReadyErrorCodes.AudioTooShort
                or ReleaseReadyErrorCodes.AudioTooLong
                    ? "audio"
                    : "metadata",
        };
    }

    private static ReleaseReadyError MapArtworkIssue(string issue, bool provided)
    {
        var code = !provided && issue.StartsWith("No artwork provided", StringComparison.OrdinalIgnoreCase)
            ? ReleaseReadyErrorCodes.MissingCoverArt
            : ReleaseReadyErrorCodes.ValidationFailed;

        return new ReleaseReadyError
        {
            Code = code,
            Message = issue,
            Field = "artwork",
        };
    }
}
