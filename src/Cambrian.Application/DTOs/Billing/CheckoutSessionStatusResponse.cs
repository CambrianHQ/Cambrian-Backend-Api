namespace Cambrian.Application.DTOs.Billing;

public class CheckoutSessionStatusResponse
{
    /// <summary>"paid", "pending", or "failed"</summary>
    public string Status { get; set; } = "pending";

    /// <summary>The tier that was activated (null if payment not completed)</summary>
    public string? Tier { get; set; }

    public string SessionId { get; set; } = "";
}
