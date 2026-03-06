namespace Cambrian.Application.DTOs.Payments;

public class PaymentStateResponse
{
    public string Status { get; set; } = "pending";

    public List<string> PurchaseIds { get; set; } = [];

    public List<string> ProcessedEventIds { get; set; } = [];
}
