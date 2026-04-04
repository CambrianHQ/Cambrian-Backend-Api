namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiLicenseOptionDto
{
    public string LicenseType { get; set; } = string.Empty; // non_exclusive | exclusive | copyright_buyout
    public string DisplayName { get; set; } = string.Empty;

    public decimal Price { get; set; }
    public string Currency { get; set; } = "usd";

    public bool CommercialUse { get; set; }
    public bool AttributionRequired { get; set; }
    public bool InstantDownload { get; set; }

    public string Exclusivity { get; set; } = "non_exclusive"; // non_exclusive | exclusive

    public string Summary { get; set; } = string.Empty;

    public List<string> AllowedUseCases { get; set; } = new();
    public List<string> Restrictions { get; set; } = new();

    public List<string> RecommendedFor { get; set; } = new();
}
