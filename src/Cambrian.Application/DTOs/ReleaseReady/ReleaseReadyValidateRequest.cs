using Microsoft.AspNetCore.Http;

namespace Cambrian.Application.DTOs.ReleaseReady;

/// <summary>
/// Multipart form body for <c>POST /release-ready/validate</c>. Bound as a single
/// <c>[FromForm]</c> model so Swashbuckle can describe the file upload — mixing
/// multiple <c>[FromForm]</c> scalar parameters with <see cref="IFormFile"/> throws
/// during swagger generation. Mirrors the established UploadTrackRequest pattern.
/// </summary>
public sealed class ReleaseReadyValidateRequest
{
    /// <summary>The master audio file to validate (required).</summary>
    public IFormFile? Audio { get; set; }

    /// <summary>Optional cover artwork accompanying the master.</summary>
    public IFormFile? Artwork { get; set; }

    /// <summary>Optional existing track to associate the master with.</summary>
    public Guid? TrackId { get; set; }

    /// <summary>Whether the master was AI-generated (drives disclosure handling).</summary>
    public bool AiGenerated { get; set; }

    /// <summary>Optional free-text AI disclosure.</summary>
    public string? AiDisclosure { get; set; }

    /// <summary>Optional target integrated loudness (LUFS) for mastering.</summary>
    public double? TargetLufs { get; set; }
}
