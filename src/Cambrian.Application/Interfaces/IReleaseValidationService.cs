using Cambrian.Application.DTOs.ReleaseReady;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Pre-mastering validation. Metadata is read from the uploaded audio's tags
/// (TagLibSharp); artwork is inspected with ImageSharp. Results are surfaced to
/// the creator before any credit is spent.
/// </summary>
public interface IReleaseValidationService
{
    /// <summary>Validate required tag fields and flag placeholder/tool-name junk.
    /// The stream must be seekable; it is read from the start.</summary>
    MetadataValidationResult ValidateMetadata(Stream audio, string fileName);

    /// <summary>Validate cover art: ≥3000×3000, JPEG/PNG, RGB. Null image fails.
    /// The stream must be seekable; it is read from the start.</summary>
    ArtworkValidationResult ValidateArtwork(Stream? image, string? fileName);
}
