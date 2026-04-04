namespace Cambrian.Application.AI.Discovery.Dtos;

public class TrackAttributesDto
{
    public string? Genre { get; set; }
    public string? Mood { get; set; }
    public string? Tempo { get; set; }
    public bool Instrumental { get; set; }
    public string? Duration { get; set; }
    public List<string> Tags { get; set; } = new();
}
