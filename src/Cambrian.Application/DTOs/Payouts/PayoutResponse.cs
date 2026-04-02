namespace Cambrian.Application.DTOs.Payouts;

public class PayoutResponse
{
    public decimal Amount { get; set; }

    public string Status { get; set; } = "pending";

    public DateTime RequestedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}