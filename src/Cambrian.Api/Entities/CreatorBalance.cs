namespace Cambrian.Api.Entities;

public class CreatorBalance
{
    public Guid Id { get; set; }

    public Guid CreatorId { get; set; }

    public decimal AvailableBalance { get; set; }

    public decimal PendingBalance { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
