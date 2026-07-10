using System.ComponentModel.DataAnnotations;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.V1;

/// <summary>Create an album. Only the title is required.</summary>
public sealed class CreateAlbumV1Request
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    [SafeMetadata]
    public string? Title { get; set; }

    [StringLength(2000)]
    [SafeMetadata]
    public string? Description { get; set; }

    [StringLength(500)]
    public string? ArtworkUrl { get; set; }

    /// <summary>draft | public | unlisted | private. Defaults to public when omitted.</summary>
    [StringLength(20)]
    public string? Visibility { get; set; }

    /// <summary>Optional ISO-8601 release date. Empty string is treated as none.</summary>
    [StringLength(40)]
    public string? ReleaseDate { get; set; }

    /// <summary>Initial track ids, in album order. Must all belong to the creator.</summary>
    public List<string>? TrackIds { get; set; }
}

/// <summary>
/// Partial album update (PATCH). Every field is optional; omitted/null fields
/// keep their stored value. Track membership is managed via the dedicated
/// tracks sub-resource, not here.
/// </summary>
public sealed class UpdateAlbumV1Request
{
    [StringLength(200, MinimumLength = 1)]
    [SafeMetadata]
    public string? Title { get; set; }

    [StringLength(2000)]
    [SafeMetadata]
    public string? Description { get; set; }

    [StringLength(500)]
    public string? ArtworkUrl { get; set; }

    /// <summary>draft | public | unlisted | private. Omit to keep the stored value.</summary>
    [StringLength(20)]
    public string? Visibility { get; set; }

    /// <summary>ISO-8601 release date. Omit to keep; empty string clears it.</summary>
    [StringLength(40)]
    public string? ReleaseDate { get; set; }
}

/// <summary>Append tracks to an album (existing order and members are preserved).</summary>
public sealed class AddAlbumTracksV1Request
{
    /// <summary>Track ids to append, in order. Must all belong to the creator.</summary>
    [Required]
    public List<string>? TrackIds { get; set; }
}

/// <summary>Reorder an album's tracks. The list must be a permutation of the current members.</summary>
public sealed class ReorderAlbumTracksV1Request
{
    /// <summary>The full set of current track ids in the desired new order.</summary>
    [Required]
    public List<string>? TrackIds { get; set; }
}
