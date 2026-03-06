namespace Cambrian.Application.DTOs.Billing;

public class BillingStatusResponse
{
    public string Tier { get; set; } = "free";

    public string Status { get; set; } = "active";

    public DateTime? ExpiresAt { get; set; }
}
