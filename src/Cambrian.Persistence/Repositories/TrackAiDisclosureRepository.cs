using System.Text.Json;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public sealed class TrackAiDisclosureRepository : ITrackAiDisclosureRepository
{
    private const string GeneratedDefinition = "Generative AI produced the entirety or primary creative portion of the sound recording, including AI lead vocals, key AI instrumental performances, or an essentially prompt-generated recording.";
    private const string AssistedDefinition = "Humans substantially created and performed the recording, while generative AI contributed some expressive elements.";
    private const string UnclassifiedDefinition = "The creator has not yet classified this track under the July 10, 2026 AI disclosure definitions.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CambrianDbContext _db;

    public TrackAiDisclosureRepository(CambrianDbContext db) => _db = db;

    public async Task<PublicTrackAiDisclosureDto> GetPublicAsync(Guid trackId)
    {
        var entity = await _db.TrackAiDisclosures.AsNoTracking().FirstOrDefaultAsync(x => x.TrackId == trackId);
        return entity is null ? Unclassified(trackId) : Map(entity);
    }

    public async Task<IReadOnlyList<TrackAiDisclosureRevisionDto>> GetHistoryAsync(Guid trackId)
    {
        var rows = await _db.TrackAiDisclosureRevisions.AsNoTracking()
            .Where(x => x.TrackId == trackId).OrderByDescending(x => x.Version).ToListAsync();
        return rows.Select(x => new TrackAiDisclosureRevisionDto
        {
            Version = x.Version,
            Action = x.Action,
            Snapshot = JsonSerializer.Deserialize<PublicTrackAiDisclosureDto>(x.SnapshotJson, JsonOptions)!,
            Reason = x.Reason,
            ChangedAt = x.ChangedAt,
        }).ToList();
    }

    public async Task<DisclosureWriteResult> CreateAsync(Guid trackId, string userId, UpsertTrackAiDisclosureRequest request)
    {
        if (await _db.TrackAiDisclosures.AnyAsync(x => x.TrackId == trackId))
            return new(DisclosureWriteStatus.AlreadyExists);

        var now = DateTime.UtcNow;
        var entity = new TrackAiDisclosure { TrackId = trackId, Version = 1, CreatedAt = now, CreatedByUserId = userId };
        Apply(entity, request, userId, now);
        _db.TrackAiDisclosures.Add(entity);
        AddRevision(entity, "Created", userId, request.CorrectionReason, now);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException) { return new(DisclosureWriteStatus.AlreadyExists); }
        return new(DisclosureWriteStatus.Success, Map(entity));
    }

    public async Task<DisclosureWriteResult> UpdateAsync(Guid trackId, string userId, UpsertTrackAiDisclosureRequest request)
    {
        var entity = await _db.TrackAiDisclosures.FirstOrDefaultAsync(x => x.TrackId == trackId);
        if (entity is null) return new(DisclosureWriteStatus.NotFound);
        if (request.ExpectedVersion.HasValue && request.ExpectedVersion != entity.Version)
            return new(DisclosureWriteStatus.VersionConflict, Map(entity));

        entity.Version++;
        var now = DateTime.UtcNow;
        Apply(entity, request, userId, now);
        entity.IsRevoked = false;
        entity.RevokedAt = null;
        AddRevision(entity, string.IsNullOrWhiteSpace(request.CorrectionReason) ? "Updated" : "Corrected", userId, request.CorrectionReason, now);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return new(DisclosureWriteStatus.VersionConflict); }
        return new(DisclosureWriteStatus.Success, Map(entity));
    }

    public async Task<DisclosureWriteResult> RevokeAsync(Guid trackId, string userId, RevokeTrackAiDisclosureRequest request)
    {
        var entity = await _db.TrackAiDisclosures.FirstOrDefaultAsync(x => x.TrackId == trackId);
        if (entity is null) return new(DisclosureWriteStatus.NotFound);
        if (request.ExpectedVersion.HasValue && request.ExpectedVersion != entity.Version)
            return new(DisclosureWriteStatus.VersionConflict, Map(entity));

        entity.Version++;
        entity.IsRevoked = true;
        entity.CorrectionReason = request.Reason.Trim();
        entity.RevokedAt = entity.UpdatedAt = DateTime.UtcNow;
        entity.UpdatedByUserId = userId;
        AddRevision(entity, "Revoked", userId, entity.CorrectionReason, entity.UpdatedAt);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return new(DisclosureWriteStatus.VersionConflict); }
        return new(DisclosureWriteStatus.Success, Map(entity));
    }

    private void AddRevision(TrackAiDisclosure entity, string action, string userId, string? reason, DateTime now)
    {
        _db.TrackAiDisclosureRevisions.Add(new TrackAiDisclosureRevision
        {
            Id = Guid.NewGuid(), TrackId = entity.TrackId, Version = entity.Version, Action = action,
            SnapshotJson = JsonSerializer.Serialize(Map(entity), JsonOptions), ChangedByUserId = userId,
            Reason = Normalize(reason), ChangedAt = now,
        });
    }

    private static void Apply(TrackAiDisclosure e, UpsertTrackAiDisclosureRequest r, string userId, DateTime now)
    {
        e.Classification = r.Classification;
        e.AiVocals = r.AiVocals; e.AiPrimaryInstruments = r.AiPrimaryInstruments;
        e.AiComposition = r.AiComposition; e.AiLyrics = r.AiLyrics; e.AiPostProduction = r.AiPostProduction;
        e.AiArtwork = r.AiArtwork; e.AiVideo = r.AiVideo;
        e.GeneratorTool = Normalize(r.GeneratorTool); e.ModelVersion = Normalize(r.ModelVersion);
        e.CreationDate = r.CreationDate; e.CommercialUseLicenseBasis = Normalize(r.CommercialUseLicenseBasis);
        e.VoiceLikenessAuthorization = Normalize(r.VoiceLikenessAuthorization);
        e.HumanWrittenLyrics = r.HumanWrittenLyrics; e.HumanVocals = r.HumanVocals;
        e.HumanInstruments = r.HumanInstruments; e.ArrangementEditing = r.ArrangementEditing; e.DawWork = r.DawWork;
        var collaborators = NormalizeCollaborators(r.Collaborators);
        e.CollaboratorsJson = collaborators.Count == 0 ? null : JsonSerializer.Serialize(collaborators, JsonOptions);
        e.HumanContributionNarrative = Normalize(r.HumanContributionNarrative);
        e.CorrectionReason = Normalize(r.CorrectionReason);
        e.UpdatedByUserId = userId; e.UpdatedAt = now;
    }

    private static PublicTrackAiDisclosureDto Map(TrackAiDisclosure e)
    {
        var revoked = e.IsRevoked;
        return new PublicTrackAiDisclosureDto
        {
            TrackId = e.TrackId.ToString(),
            Classification = revoked ? AiTrackClassification.Unclassified : e.Classification,
            Definition = Definition(revoked ? AiTrackClassification.Unclassified : e.Classification),
            Details = revoked ? new() : new AiDisclosureDetailsDto
            {
                AiVocals = e.AiVocals, AiPrimaryInstruments = e.AiPrimaryInstruments, AiComposition = e.AiComposition,
                AiLyrics = e.AiLyrics, AiPostProduction = e.AiPostProduction, AiArtwork = e.AiArtwork, AiVideo = e.AiVideo,
                GeneratorTool = e.GeneratorTool, ModelVersion = e.ModelVersion, CreationDate = e.CreationDate,
                CommercialUseLicenseBasis = e.CommercialUseLicenseBasis, VoiceLikenessAuthorization = e.VoiceLikenessAuthorization,
                HumanWrittenLyrics = e.HumanWrittenLyrics, HumanVocals = e.HumanVocals, HumanInstruments = e.HumanInstruments,
                ArrangementEditing = e.ArrangementEditing, DawWork = e.DawWork,
                Collaborators = DeserializeCollaborators(e.CollaboratorsJson), HumanContributionNarrative = e.HumanContributionNarrative,
            },
            Version = e.Version, IsRevoked = revoked, CorrectionNotice = e.CorrectionReason,
            RevokedAt = e.RevokedAt, UpdatedAt = e.UpdatedAt,
        };
    }

    private static PublicTrackAiDisclosureDto Unclassified(Guid trackId) => new()
    {
        TrackId = trackId.ToString(), Classification = AiTrackClassification.Unclassified,
        Definition = UnclassifiedDefinition, Version = 0,
    };

    private static string Definition(AiTrackClassification value) => value switch
    {
        AiTrackClassification.AIGenerated => GeneratedDefinition,
        AiTrackClassification.AIAssisted => AssistedDefinition,
        _ => UnclassifiedDefinition,
    };

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IReadOnlyList<string> NormalizeCollaborators(IEnumerable<string>? values) => (values ?? Array.Empty<string>())
        .Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static IReadOnlyList<string> DeserializeCollaborators(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}
