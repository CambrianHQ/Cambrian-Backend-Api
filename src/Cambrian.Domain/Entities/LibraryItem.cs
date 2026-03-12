namespace Cambrian.Domain.Entities;

public class LibraryItem
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = "";

    public ApplicationUser User { get; set; } = null!;

    public Guid TrackId { get; set; }

    public Track Track { get; set; } = null!;

    /// <summary>FK to the purchase that granted access (null for legacy / free items).</summary>
    public Guid? PurchaseId { get; set; }

    public Purchase? Purchase { get; set; }

    public string? Title { get; set; }

    public string? Artist { get; set; }

    public string? AudioUrl { get; set; }

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}