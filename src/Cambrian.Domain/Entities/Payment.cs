namespace Cambrian.Domain.Entities;

public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid PurchaseId { get; set; }
    public string ProviderReference { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
}
