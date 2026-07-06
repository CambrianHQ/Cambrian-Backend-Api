namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiTrackPreview
{
    public bool Available { get; set; }
    public string? Url { get; set; }

    public int? DurationSeconds { get; set; }
    public int? ClipStartSeconds { get; set; }
    public int? ClipEndSeconds { get; set; }

    public string? Format { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
