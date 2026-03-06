namespace Cambrian.Application.DTOs.Catalog;

public class TrackResponse
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Title { get; set; } = string.Empty;

    public string Genre { get; set; } = string.Empty;

    public decimal Price { get; set; }
}