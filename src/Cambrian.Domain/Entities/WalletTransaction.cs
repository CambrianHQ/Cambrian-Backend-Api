namespace Cambrian.Domain.Entities;

public class WalletTransaction
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = "";

    public ApplicationUser User { get; set; } = null!;

    public long AmountCents { get; set; }

    public string Type { get; set; } = ""; // credit, debit, withdrawal

    public string? Description { get; set; }

    public Guid? RelatedPurchaseId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
