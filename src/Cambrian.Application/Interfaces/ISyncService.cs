using Cambrian.Application.DTOs.Sync;

namespace Cambrian.Application.Interfaces;

public interface ISyncService
{
    Task<SyncBriefDto> CreateBriefAsync(string buyerUserId, CreateSyncBriefRequest request);
    Task<List<SyncBriefDto>> ListBriefsAsync(string? genre, decimal? minBudget, decimal? maxBudget, int page, int pageSize);
    Task<SyncBriefDetailDto?> GetBriefAsync(Guid briefId);
    Task<SyncSubmissionDto?> SubmitToBriefAsync(Guid briefId, string creatorUserId, SubmitToSyncBriefRequest request);
    Task<bool> SelectSubmissionAsync(Guid briefId, Guid submissionId, string buyerUserId);
}
