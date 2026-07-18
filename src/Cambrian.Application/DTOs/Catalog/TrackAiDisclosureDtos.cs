using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.DTOs.Catalog;

public sealed class PublicTrackAiDisclosureDto
{
    public string TrackId { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AiTrackClassification Classification { get; set; }
    public string Definition { get; set; } = string.Empty;
    public AiDisclosureDetailsDto Details { get; set; } = new();
    public int Version { get; set; }
    public bool IsRevoked { get; set; }
    public string? CorrectionNotice { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class AiDisclosureDetailsDto
{
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
    public IReadOnlyList<string> Collaborators { get; set; } = Array.Empty<string>();
    public string? HumanContributionNarrative { get; set; }
}

public sealed class UpsertTrackAiDisclosureRequest
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [EnumDataType(typeof(AiTrackClassification))]
    public AiTrackClassification Classification { get; set; }
    public bool? AiVocals { get; set; }
    public bool? AiPrimaryInstruments { get; set; }
    public bool? AiComposition { get; set; }
    public bool? AiLyrics { get; set; }
    public bool? AiPostProduction { get; set; }
    public bool? AiArtwork { get; set; }
    public bool? AiVideo { get; set; }
    [MaxLength(200)] public string? GeneratorTool { get; set; }
    [MaxLength(200)] public string? ModelVersion { get; set; }
    public DateOnly? CreationDate { get; set; }
    [MaxLength(2000)] public string? CommercialUseLicenseBasis { get; set; }
    [MaxLength(2000)] public string? VoiceLikenessAuthorization { get; set; }
    public bool? HumanWrittenLyrics { get; set; }
    public bool? HumanVocals { get; set; }
    public bool? HumanInstruments { get; set; }
    public bool? ArrangementEditing { get; set; }
    public bool? DawWork { get; set; }
    [MaxLength(50)] public List<string>? Collaborators { get; set; }
    [MaxLength(5000)] public string? HumanContributionNarrative { get; set; }
    public int? ExpectedVersion { get; set; }
    [MaxLength(1000)] public string? CorrectionReason { get; set; }
}

public sealed class RevokeTrackAiDisclosureRequest
{
    [Required, MaxLength(1000)] public string Reason { get; set; } = string.Empty;
    public int? ExpectedVersion { get; set; }
}

public sealed class TrackAiDisclosureRevisionDto
{
    public int Version { get; set; }
    public string Action { get; set; } = string.Empty;
    public PublicTrackAiDisclosureDto Snapshot { get; set; } = new();
    public string? Reason { get; set; }
    public DateTime ChangedAt { get; set; }
}
