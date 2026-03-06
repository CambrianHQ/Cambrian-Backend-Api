namespace Cambrian.Domain.Entities;

public class Subscription
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = "";

    public ApplicationUser User { get; set; } = null!;

    public string Plan { get; set; } = "free"; // free, paid, creator

    public string Status { get; set; } = "active"; // active, cancelled, expired

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresAt { get; set; }
}
