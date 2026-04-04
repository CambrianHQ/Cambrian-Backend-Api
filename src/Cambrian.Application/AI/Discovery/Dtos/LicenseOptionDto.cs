namespace Cambrian.Application.AI.Discovery.Dtos;

public class LicenseOptionDto
{
    public string LicenseType { get; set; } = string.Empty;
    public int PriceCents { get; set; }
    public decimal PriceDollars { get; set; }
    public bool Available { get; set; }
    public List<string> AllowedUses { get; set; } = new();
    public List<string> Restrictions { get; set; } = new();
}
