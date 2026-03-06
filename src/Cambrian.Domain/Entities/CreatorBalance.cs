namespace Cambrian.Domain.Entities;

public sealed class CreatorBalance
{
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public decimal AvailableAmount { get; set; }
}
