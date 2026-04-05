using Cambrian.Application.DTOs.Sync;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Services;

public class SyncService : ISyncService
{
    private readonly CambrianDbContext _db;

    public SyncService(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<SyncBriefDto> CreateBriefAsync(string buyerUserId, CreateSyncBriefRequest request)
    {
        var brief = new SyncBrief
        {
            Id = Guid.NewGuid(),
            BuyerUserId = buyerUserId,
            Title = request.Title,
            Description = request.Description,
            Genre = request.Genre,
            Mood = request.Mood,
            Budget = request.Budget,
            Deadline = request.Deadline,
            UsageType = request.UsageType,
            Territory = request.Territory,
            Status = "open",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.SyncBriefs.Add(brief);
        await _db.SaveChangesAsync();

        return MapBriefToDto(brief, 0);
    }

    public async Task<List<SyncBriefDto>> ListBriefsAsync(string? genre, decimal? minBudget, decimal? maxBudget, int page, int pageSize)
    {
        var query = _db.SyncBriefs
            .Where(b => b.Status == "open");

        if (!string.IsNullOrWhiteSpace(genre))
            query = query.Where(b => b.Genre != null && b.Genre.ToLower() == genre.ToLower());

        if (minBudget.HasValue)
            query = query.Where(b => b.Budget >= minBudget.Value);

        if (maxBudget.HasValue)
            query = query.Where(b => b.Budget <= maxBudget.Value);

        var briefs = await query
            .OrderByDescending(b => b.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => new SyncBriefDto
            {
                Id = b.Id,
                BuyerUserId = b.BuyerUserId,
                Title = b.Title,
                Description = b.Description,
                Genre = b.Genre,
                Mood = b.Mood,
                Budget = b.Budget,
                Deadline = b.Deadline,
                UsageType = b.UsageType,
                Territory = b.Territory,
                Status = b.Status,
                SubmissionCount = b.Submissions.Count,
                CreatedAt = b.CreatedAt
            })
            .ToListAsync();

        return briefs;
    }

    public async Task<SyncBriefDetailDto?> GetBriefAsync(Guid briefId)
    {
        var brief = await _db.SyncBriefs
            .Include(b => b.Submissions)
            .FirstOrDefaultAsync(b => b.Id == briefId);

        if (brief is null) return null;

        return new SyncBriefDetailDto
        {
            Id = brief.Id,
            BuyerUserId = brief.BuyerUserId,
            Title = brief.Title,
            Description = brief.Description,
            Genre = brief.Genre,
            Mood = brief.Mood,
            Budget = brief.Budget,
            Deadline = brief.Deadline,
            UsageType = brief.UsageType,
            Territory = brief.Territory,
            Status = brief.Status,
            SubmissionCount = brief.Submissions.Count,
            CreatedAt = brief.CreatedAt,
            Submissions = brief.Submissions.Select(s => new SyncSubmissionDto
            {
                Id = s.Id,
                CreatorUserId = s.CreatorUserId,
                TrackId = s.TrackId,
                Note = s.Note,
                SubmittedAt = s.SubmittedAt,
                Status = s.Status
            }).ToList()
        };
    }

    public async Task<SyncSubmissionDto?> SubmitToBriefAsync(Guid briefId, string creatorUserId, SubmitToSyncBriefRequest request)
    {
        var brief = await _db.SyncBriefs.FindAsync(briefId);
        if (brief is null || brief.Status != "open") return null;

        var submission = new SyncSubmission
        {
            Id = Guid.NewGuid(),
            SyncBriefId = briefId,
            CreatorUserId = creatorUserId,
            TrackId = request.TrackId,
            Note = request.Note,
            SubmittedAt = DateTime.UtcNow,
            Status = "pending"
        };

        _db.SyncSubmissions.Add(submission);
        await _db.SaveChangesAsync();

        return new SyncSubmissionDto
        {
            Id = submission.Id,
            CreatorUserId = submission.CreatorUserId,
            TrackId = submission.TrackId,
            Note = submission.Note,
            SubmittedAt = submission.SubmittedAt,
            Status = submission.Status
        };
    }

    public async Task<bool> SelectSubmissionAsync(Guid briefId, Guid submissionId, string buyerUserId)
    {
        var brief = await _db.SyncBriefs
            .Include(b => b.Submissions)
            .FirstOrDefaultAsync(b => b.Id == briefId);

        if (brief is null || brief.BuyerUserId != buyerUserId) return false;

        var submission = brief.Submissions.FirstOrDefault(s => s.Id == submissionId);
        if (submission is null) return false;

        submission.Status = "selected";
        brief.Status = "filled";
        brief.UpdatedAt = DateTime.UtcNow;

        foreach (var other in brief.Submissions.Where(s => s.Id != submissionId))
            other.Status = "rejected";

        await _db.SaveChangesAsync();
        return true;
    }

    private static SyncBriefDto MapBriefToDto(SyncBrief brief, int submissionCount) => new()
    {
        Id = brief.Id,
        BuyerUserId = brief.BuyerUserId,
        Title = brief.Title,
        Description = brief.Description,
        Genre = brief.Genre,
        Mood = brief.Mood,
        Budget = brief.Budget,
        Deadline = brief.Deadline,
        UsageType = brief.UsageType,
        Territory = brief.Territory,
        Status = brief.Status,
        SubmissionCount = submissionCount,
        CreatedAt = brief.CreatedAt
    };
}
