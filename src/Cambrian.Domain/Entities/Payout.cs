namespace Cambrian.Domain.Entities;

public class Payout
{
    public Guid Id { get; set; }

    public string CreatorId { get; set; } = "";

    public ApplicationUser Creator { get; set; } = null!;

    public double Amount { get; set; }

    public string Status { get; set; } = "pending"; // pending, approved, rejected, completed

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
}