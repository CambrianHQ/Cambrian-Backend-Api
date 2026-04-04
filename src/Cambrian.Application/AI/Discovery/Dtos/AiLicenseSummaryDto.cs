namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiLicenseSummaryDto
{
    public string CheapestLicenseType { get; set; } = string.Empty;
    public decimal CheapestPrice { get; set; }
    public string Currency { get; set; } = "usd";
    public bool ExclusiveAvailable { get; set; }
    public bool CopyrightBuyoutAvailable { get; set; }
    public List<AiLicenseOptionDto> Options { get; set; } = new();
}
