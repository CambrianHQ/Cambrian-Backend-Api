namespace Cambrian.Application.DTOs.Payouts;

public class PayoutResponse
{
    public decimal Amount { get; set; }

    public string Status { get; set; } = "pending";
}
