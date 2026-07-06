using System.ComponentModel.DataAnnotations;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.CreatorProfile;

public class UpsertCollectionRequest
{
    [StringLength(200)]
    [SafeMetadata]
    public string? Title { get; set; }

    [StringLength(2000)]
    [SafeMetadata]
    public string? Description { get; set; }

    [StringLength(500)]
    public string? CoverImageUrl { get; set; }

    /// <summary>Comma-separated track GUIDs in display order (order = positions).</summary>
    [StringLength(5000)]
    public string? TrackIds { get; set; }

    /// <summary>Album visibility: public | hidden. Omit to keep the stored value.</summary>
    [StringLength(20)]
    public string? Visibility { get; set; }

    /// <summary>Optional release date (ISO-8601). Omit to keep; empty string clears.</summary>
    [StringLength(40)]
    public string? ReleaseDate { get; set; }
}
