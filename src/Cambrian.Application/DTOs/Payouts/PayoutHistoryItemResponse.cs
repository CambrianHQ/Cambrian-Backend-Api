namespace Cambrian.Application.DTOs.Payouts;

public class PayoutHistoryItemResponse
{
    public string Id { get; set; } = "";

    public decimal Amount { get; set; }

    public string Status { get; set; } = "";

    public DateTime RequestedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
