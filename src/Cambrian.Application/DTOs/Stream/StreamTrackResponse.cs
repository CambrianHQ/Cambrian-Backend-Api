namespace Cambrian.Application.DTOs.Stream;

public class StreamTrackResponse
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Artist { get; set; } = "";

    public string? Genre { get; set; }

    public string? Duration { get; set; }

    public string? AudioUrl { get; set; }
}
