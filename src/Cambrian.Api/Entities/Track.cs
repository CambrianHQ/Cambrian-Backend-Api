namespace Cambrian.Api.Entities;

public class Track
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Genre { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public Guid CreatorId { get; set; }

    public decimal NonExclusivePrice { get; set; }

    public decimal ExclusivePrice { get; set; }

    public bool ExclusiveSold { get; set; }

    public string AudioUrl { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
