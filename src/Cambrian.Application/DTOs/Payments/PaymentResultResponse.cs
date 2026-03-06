namespace Cambrian.Application.DTOs.Payments;

public class PaymentResultResponse
{
    public string Status { get; set; } = "pending";

    public string? PurchaseId { get; set; }

    public string? EventId { get; set; }

    public string? EventType { get; set; }

    public bool Duplicate { get; set; }
}
