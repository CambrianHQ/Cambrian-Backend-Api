namespace Cambrian.Domain.Entities;

public class Invoice
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = "";

    public ApplicationUser User { get; set; } = null!;

    public Guid PurchaseId { get; set; }

    public Purchase Purchase { get; set; } = null!;

    public int AmountCents { get; set; }

    public string Currency { get; set; } = "usd";

    public string Status { get; set; } = "issued"; // issued, paid, voided

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PaidAt { get; set; }
}
