namespace Cambrian.Api.Entities;

public class LibraryItem
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid TrackId { get; set; }

    public Guid PurchaseId { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
