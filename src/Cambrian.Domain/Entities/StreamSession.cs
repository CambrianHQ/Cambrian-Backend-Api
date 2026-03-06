namespace Cambrian.Domain.Entities;

public class StreamSession
{
    public Guid Id { get; set; }

    public Guid TrackId { get; set; }

    public Track Track { get; set; } = null!;

    public string? UserId { get; set; }

    public string? Title { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StoppedAt { get; set; }
}
