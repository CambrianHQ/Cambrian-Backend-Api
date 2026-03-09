namespace Cambrian.Application.DTOs.Subscriptions;

public class SubscriptionPlanResponse
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public decimal PriceMonthly { get; set; }

    public IReadOnlyCollection<string> Features { get; set; } = Array.Empty<string>();
}
