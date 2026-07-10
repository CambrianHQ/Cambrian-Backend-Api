namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiTrackAttributes
{
    public string? Genre { get; set; }
    public string? Subgenre { get; set; }

    public List<string> Moods { get; set; } = new();

    public int Bpm { get; set; }
    public string? Key { get; set; }

    public int DurationSeconds { get; set; }

    public bool Instrumental { get; set; }
    public bool HasVocals { get; set; }

    public string? VocalsType { get; set; }

    public string? EnergyLevel { get; set; }
    public string? EnergyCurve { get; set; }

    public bool LoopFriendly { get; set; }
    public int? DropMomentSeconds { get; set; }
}
