namespace Cambrian.Domain.Entities;

public class Track
{
    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    public string? Genre { get; set; }

    public double Price { get; set; }

    public string? Duration { get; set; }

    public string? LicenseType { get; set; }

    public string? AudioUrl { get; set; }

    public int NonExclusivePriceCents { get; set; }

    public int ExclusivePriceCents { get; set; }

    public string Visibility { get; set; } = "public"; // public, limited, hidden

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string CreatorId { get; set; } = "";

    public ApplicationUser Creator { get; set; } = null!;

    public ICollection<string> Tags { get; set; } = new List<string>();

    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();

    public ICollection<LibraryItem> LibraryItems { get; set; } = new List<LibraryItem>();
}
