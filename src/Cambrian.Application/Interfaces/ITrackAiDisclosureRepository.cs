using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

public enum DisclosureWriteStatus { Success, AlreadyExists, NotFound, VersionConflict }

public sealed record DisclosureWriteResult(DisclosureWriteStatus Status, PublicTrackAiDisclosureDto? Disclosure = null);

public interface ITrackAiDisclosureRepository
{
    Task<PublicTrackAiDisclosureDto> GetPublicAsync(Guid trackId);
    Task<IReadOnlyList<TrackAiDisclosureRevisionDto>> GetHistoryAsync(Guid trackId);
    Task<DisclosureWriteResult> CreateAsync(Guid trackId, string userId, UpsertTrackAiDisclosureRequest request);
    Task<DisclosureWriteResult> UpdateAsync(Guid trackId, string userId, UpsertTrackAiDisclosureRequest request);
    Task<DisclosureWriteResult> RevokeAsync(Guid trackId, string userId, RevokeTrackAiDisclosureRequest request);
}
