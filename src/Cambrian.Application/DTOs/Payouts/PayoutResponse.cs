namespace Cambrian.Application.DTOs.Payouts;

public class PayoutResponse
{
    public Guid Id { get; set; }

    public decimal Amount { get; set; }

    public string Status { get; set; } = "pending";

    public DateTime RequestedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? FailureReason { get; set; }
}
