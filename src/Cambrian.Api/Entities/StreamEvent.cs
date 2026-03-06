namespace Cambrian.Api.Entities;

public class StreamEvent
{
    public Guid Id { get; set; }

    public Guid TrackId { get; set; }

    public Guid UserId { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? EndedAt { get; set; }

    public int Duration { get; set; }
}
