namespace Cambrian.Application.DTOs.Subscriptions;

public class CurrentSubscriptionResponse
{
    public string Plan { get; set; } = "free";

    public string Status { get; set; } = "active";

    public string NextBilling { get; set; } = "";
}
