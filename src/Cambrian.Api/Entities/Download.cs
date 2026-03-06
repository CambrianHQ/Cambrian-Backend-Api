namespace Cambrian.Api.Entities;

public class Download
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid TrackId { get; set; }

    public string IpAddress { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
