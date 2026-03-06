namespace Cambrian.Application.DTOs.Invoices;

public class InvoiceResponse
{
    public string Id { get; set; } = "";

    public string PurchaseId { get; set; } = "";

    public int AmountCents { get; set; }

    public string Currency { get; set; } = "usd";

    public string Status { get; set; } = "issued";

    public DateTime IssuedAt { get; set; }

    public DateTime? PaidAt { get; set; }
}
