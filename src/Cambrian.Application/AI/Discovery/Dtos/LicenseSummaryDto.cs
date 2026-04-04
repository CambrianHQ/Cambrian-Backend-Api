namespace Cambrian.Application.AI.Discovery.Dtos;

public class LicenseSummaryDto
{
    public string CheapestLicenseType { get; set; } = string.Empty;
    public int CheapestPriceCents { get; set; }
    public decimal CheapestPriceDollars { get; set; }
    public bool ExclusiveAvailable { get; set; }
    public bool CopyrightBuyoutAvailable { get; set; }
    public List<LicenseOptionDto> Options { get; set; } = new();
}
