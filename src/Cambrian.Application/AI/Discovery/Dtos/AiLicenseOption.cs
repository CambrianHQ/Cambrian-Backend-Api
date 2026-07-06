namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiLicenseOption
{
    public string DisplayName { get; set; } = string.Empty;

    public decimal Price { get; set; }
    public string Currency { get; set; } = "usd";

    public bool CommercialUse { get; set; }
    public bool AttributionRequired { get; set; }
    public bool InstantDownload { get; set; }

    public string Summary { get; set; } = string.Empty;

    public List<string> AllowedUseCases { get; set; } = new();
    public List<string> Restrictions { get; set; } = new();

    public List<string> RecommendedFor { get; set; } = new();
}
