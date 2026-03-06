namespace Cambrian.Application.DTOs.Catalog;

public class UploadTrackRequest
{
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Genre { get; set; } = string.Empty;

    public decimal Price { get; set; }
}
