namespace Cambrian.Application.DTOs.Purchases;

public class CreditCreatorRequest
{
    public string? CreatorId { get; set; }

    public string? TrackId { get; set; }

    public string? TrackTitle { get; set; }

    public int AmountCents { get; set; }

    public string? LicenseType { get; set; }
}
