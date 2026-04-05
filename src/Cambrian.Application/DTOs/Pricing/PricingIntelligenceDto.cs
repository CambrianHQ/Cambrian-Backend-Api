namespace Cambrian.Application.DTOs.Pricing;

public class PricingIntelligenceDto
{
    public string Genre { get; set; } = null!;
    public int AveragePriceCents { get; set; }
    public int MedianPriceCents { get; set; }
    public int MinPriceCents { get; set; }
    public int MaxPriceCents { get; set; }
    public PriceDistributionDto Distribution { get; set; } = new();
    public int TotalTracksInGenre { get; set; }
}

public class PriceDistributionDto
{
    public int Bucket0To10 { get; set; }
    public int Bucket10To25 { get; set; }
    public int Bucket25To50 { get; set; }
    public int Bucket50To100 { get; set; }
    public int Bucket100Plus { get; set; }
}

public class CreatorPricingPositionDto
{
    public string Genre { get; set; } = null!;
    public int AveragePriceCents { get; set; }
    public int MedianPriceCents { get; set; }
    public int MinPriceCents { get; set; }
    public int MaxPriceCents { get; set; }
    public PriceDistributionDto Distribution { get; set; } = new();
    public int TotalTracksInGenre { get; set; }
    public int CreatorAveragePriceCents { get; set; }
    public double PercentileRank { get; set; }
}
