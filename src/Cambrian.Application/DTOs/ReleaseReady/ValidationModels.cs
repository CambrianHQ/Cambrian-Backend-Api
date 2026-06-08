namespace Cambrian.Application.DTOs.ReleaseReady;

/// <summary>Combined validation report shown to the creator before mastering.</summary>
public sealed class ValidationReport
{
    public MetadataValidationResult Metadata { get; init; } = new();
    public ArtworkValidationResult Artwork { get; init; } = new();

    /// <summary>True when both metadata and artwork pass (and artwork is present).</summary>
    public bool Passed => Metadata.Passed && Artwork.Passed;
}

public sealed class MetadataValidationResult
{
    public bool Passed { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }

    /// <summary>Human-readable problems the creator must fix.</summary>
    public List<string> Issues { get; set; } = new();

    /// <summary>Tag fields flagged as junk/placeholder that will be stripped from the master.</summary>
    public List<string> Stripped { get; set; } = new();
}

public sealed class ArtworkValidationResult
{
    public bool Passed { get; set; }
    public bool Provided { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Format { get; set; }
    public List<string> Issues { get; set; } = new();
}
