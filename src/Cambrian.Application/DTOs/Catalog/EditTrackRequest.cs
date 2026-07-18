using System.ComponentModel.DataAnnotations;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.Catalog;

public class EditTrackRequest
{
    [MaxLength(200)]
    [SafeMetadata]
    public string? Title { get; set; }

    [MaxLength(2000)]
    [SafeMetadata]
    public string? Description { get; set; }

    [MaxLength(60)]
    [SafeMetadata]
    public string? Genre { get; set; }

    [MaxLength(60)]
    [SafeMetadata]
    public string? PrimaryGenre { get; set; }

    [MaxLength(60)]
    [SafeMetadata]
    public string? Subgenre { get; set; }

    [MaxLength(50)]
    [SafeMetadata]
    public string? Mood { get; set; }

    [MaxLength(30)]
    [SafeMetadata]
    public string? Tempo { get; set; }

    [MaxLength(500)]
    [SafeMetadata]
    public string? Tags { get; set; }

    [Range(1, int.MaxValue)]
    public int? NonExclusivePriceCents { get; set; }

    [Range(1, int.MaxValue)]
    public int? ExclusivePriceCents { get; set; }

    [Range(1, int.MaxValue)]
    public int? CopyrightBuyoutPriceCents { get; set; }

    /// <summary>
    /// Publish/unpublish: "public" or "hidden". Omit to keep the stored value.
    /// This is the same in-place partial update as every other field — it can
    /// never recreate the track or touch engagement data.
    /// </summary>
    [MaxLength(20)]
    public string? Visibility { get; set; }

    /// <summary>
    /// AI-use disclosure text — the free attestation the compliance checklist's
    /// <c>ai_disclosure</c> item reads (stored on <c>TrackAuthorship.AiDisclosure</c>).
    /// "No generative AI was used" must be stated as explicit text, not inferred.
    /// Omit (null) to keep the stored value; whitespace-only clears it.
    /// </summary>
    [MaxLength(2000)]
    [SafeMetadata]
    public string? AiDisclosure { get; set; }

    /// <summary>
    /// Rights/ownership attestation — the free creator confirmation the compliance
    /// checklist's <c>rights</c> item reads (stored on <c>Track.CommercialRightsVerified</c>).
    /// A self-attestation, not a paid verification. Omit to keep the stored value.
    /// </summary>
    public bool? RightsConfirmed { get; set; }
}
