namespace Cambrian.Application.DTOs.Subscriptions;

public class PlanResponse
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public int PriceCents { get; set; }

    public string Interval { get; set; } = "month";

    public List<string> Features { get; set; } = [];
}
