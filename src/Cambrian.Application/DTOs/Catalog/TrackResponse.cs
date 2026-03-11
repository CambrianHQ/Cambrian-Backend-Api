namespace Cambrian.Application.DTOs.Catalog;

public class TrackResponse
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Genre { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public decimal NonExclusivePrice { get; set; }

    public decimal ExclusivePrice { get; set; }

    public bool ExclusiveSold { get; set; }

    public string? LicenseType { get; set; }

    public string? Duration { get; set; }

    public string? AudioUrl { get; set; }

    public string? CoverArtUrl { get; set; }

    public string CreatorId { get; set; } = string.Empty;

    public string? Artist { get; set; }

    public DateTime CreatedAt { get; set; }
}