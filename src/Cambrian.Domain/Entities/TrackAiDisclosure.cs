namespace Cambrian.Domain.Entities;

public enum AiTrackClassification
{
    Unclassified = 0,
    AIGenerated = 1,
    AIAssisted = 2,
}

/// <summary>Creator-maintained AI disclosure. Absence of this row means Unclassified.</summary>
public sealed class TrackAiDisclosure
{
    public Guid TrackId { get; set; }
    public AiTrackClassification Classification { get; set; }
    public bool? AiVocals { get; set; }
    public bool? AiPrimaryInstruments { get; set; }
    public bool? AiComposition { get; set; }
    public bool? AiLyrics { get; set; }
    public bool? AiPostProduction { get; set; }
    public bool? AiArtwork { get; set; }
    public bool? AiVideo { get; set; }
    public string? GeneratorTool { get; set; }
    public string? ModelVersion { get; set; }
    public DateOnly? CreationDate { get; set; }
    public string? CommercialUseLicenseBasis { get; set; }
    public string? VoiceLikenessAuthorization { get; set; }
    public bool? HumanWrittenLyrics { get; set; }
    public bool? HumanVocals { get; set; }
    public bool? HumanInstruments { get; set; }
    public bool? ArrangementEditing { get; set; }
    public bool? DawWork { get; set; }
    public string? CollaboratorsJson { get; set; }
    public string? HumanContributionNarrative { get; set; }
    public int Version { get; set; } = 1;
    public bool IsRevoked { get; set; }
    public string? CorrectionReason { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public string UpdatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Track Track { get; set; } = null!;
}

/// <summary>Immutable snapshot of every disclosure mutation.</summary>
public sealed class TrackAiDisclosureRevision
{
    public Guid Id { get; set; }
    public Guid TrackId { get; set; }
    public int Version { get; set; }
    public string Action { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
    public string ChangedByUserId { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime ChangedAt { get; set; }
    public Track Track { get; set; } = null!;
}
