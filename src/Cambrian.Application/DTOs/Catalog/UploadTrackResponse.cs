namespace Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Response returned after a successful track upload.
/// </summary>
public class UploadTrackResponse
{
    public string TrackId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string CambrianTrackId { get; set; } = string.Empty;

    public string? Genre { get; set; }

    public string? PrimaryGenre { get; set; }

    public string? Subgenre { get; set; }

    public string? CoverArtUrl { get; set; }

    public string? CollectionId { get; set; }

    public string? CollectionTitle { get; set; }

    public IReadOnlyList<string> CollectionTrackIds { get; set; } = Array.Empty<string>();
}
