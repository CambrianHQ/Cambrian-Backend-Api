using Microsoft.AspNetCore.Identity;

namespace Cambrian.Domain.Entities;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }

    public string Role { get; set; } = "User"; // User, Admin

    public string Status { get; set; } = "active"; // active, suspended

    public string Tier { get; set; } = "free"; // free, paid, creator

    public bool VerifiedCreator { get; set; }

    public string? Plan { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Track> Tracks { get; set; } = new List<Track>();

    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();

    public ICollection<LibraryItem> Library { get; set; } = new List<LibraryItem>();

    public ICollection<Payout> Payouts { get; set; } = new List<Payout>();
}
