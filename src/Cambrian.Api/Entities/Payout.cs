namespace Cambrian.Api.Entities;

public class Payout
{
    public Guid Id { get; set; }

    public Guid CreatorId { get; set; }

    public decimal Amount { get; set; }

    public string Status { get; set; } = "pending";

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
}
