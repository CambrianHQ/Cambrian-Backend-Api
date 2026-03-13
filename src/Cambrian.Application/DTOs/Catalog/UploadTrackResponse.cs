namespace Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Response returned after a successful track upload.
/// </summary>
public class UploadTrackResponse
{
    public string TrackId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string CambrianTrackId { get; set; } = string.Empty;
}
