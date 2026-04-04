namespace Cambrian.Application.AI.Discovery.Queries;

public class SearchTracksQuery
{
    public string? Query { get; set; }
    public string? Genre { get; set; }
    public string? Mood { get; set; }

    public int? Bpm { get; set; }
    public string? Key { get; set; }

    public bool InstrumentalOnly { get; set; }
    public bool VocalsAllowed { get; set; } = true;

    public string? AiGenerator { get; set; }
    public string? UseCase { get; set; }

    public bool CommercialUseRequired { get; set; } = true;

    public int? MinDurationSeconds { get; set; }
    public int? MaxDurationSeconds { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}
