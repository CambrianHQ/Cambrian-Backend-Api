using Cambrian.Domain.Enums;

namespace Cambrian.Domain.Entities;

public sealed class License
{
    public Guid Id { get; set; }
    public Guid TrackId { get; set; }
    public LicenseType Type { get; set; } = LicenseType.Standard;
    public decimal Price { get; set; }
}
