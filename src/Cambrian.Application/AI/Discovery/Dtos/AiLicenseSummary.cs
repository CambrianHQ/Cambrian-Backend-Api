namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiLicenseSummary
{
    public decimal StartingPrice { get; set; }
    public string Currency { get; set; } = "USD";

    public bool CommercialUse { get; set; }
    public bool AttributionRequired { get; set; }
    public bool InstantDownload { get; set; }

    public double LicenseClarityScore { get; set; }

    public List<string> CommercialSafetyNotes { get; set; } = new();
}
