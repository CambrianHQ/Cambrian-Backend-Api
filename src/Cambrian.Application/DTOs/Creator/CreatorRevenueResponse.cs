namespace Cambrian.Application.DTOs.Creator;

public class CreatorRevenueResponse
{
    public decimal TotalEarned { get; set; }

    public decimal PendingBalance { get; set; }

    public decimal PendingPayouts { get; set; }

    public decimal PaidOut { get; set; }
}
