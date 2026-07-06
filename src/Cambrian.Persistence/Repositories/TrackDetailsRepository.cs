using System.Text.Json;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class TrackDetailsRepository : ITrackDetailsRepository
{
    private readonly CambrianDbContext _db;

    public TrackDetailsRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<TrackLyricsDto?> GetLyricsAsync(Guid trackId)
    {
        var entity = await _db.TrackLyrics.AsNoTracking().FirstOrDefaultAsync(l => l.TrackId == trackId);
        return entity is null ? null : MapLyrics(entity);
    }

    public async Task<TrackLyricsDto> UpsertLyricsAsync(Guid trackId, string lyrics, string language, bool? isExplicit)
    {
        var existing = await _db.TrackLyrics.FindAsync(trackId);
        if (existing is null)
        {
            existing = new TrackLyrics
            {
                TrackId = trackId,
                Lyrics = lyrics,
                Language = language,
                IsExplicit = isExplicit,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.TrackLyrics.Add(existing);
        }
        else
        {
            existing.Lyrics = lyrics;
            existing.Language = language;
            existing.IsExplicit = isExplicit;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return MapLyrics(existing);
    }

    public async Task DeleteLyricsAsync(Guid trackId)
    {
        var existing = await _db.TrackLyrics.FindAsync(trackId);
        if (existing is not null)
        {
            _db.TrackLyrics.Remove(existing);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<BehindTheTrackDto?> GetCreationProcessAsync(Guid trackId)
    {
        var entity = await _db.TrackCreationProcesses.AsNoTracking().FirstOrDefaultAsync(p => p.TrackId == trackId);
        return entity is null ? null : MapProcess(entity);
    }

    public async Task<BehindTheTrackDto> UpsertCreationProcessAsync(
        Guid trackId,
        string? story,
        string? daw,
        string? vocalChain,
        string? promptNotes,
        string? productionNotes,
        string? humanContributionNotes,
        string? youtubeUrl,
        IReadOnlyList<string> toolsUsed)
    {
        var toolsJson = toolsUsed.Count > 0 ? JsonSerializer.Serialize(toolsUsed) : null;
        var existing = await _db.TrackCreationProcesses.FindAsync(trackId);
        if (existing is null)
        {
            existing = new TrackCreationProcess
            {
                TrackId = trackId,
                Story = story,
                DAW = daw,
                VocalChain = vocalChain,
                PromptNotes = promptNotes,
                ProductionNotes = productionNotes,
                HumanContributionNotes = humanContributionNotes,
                YoutubeUrl = youtubeUrl,
                ToolsUsed = toolsJson,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.TrackCreationProcesses.Add(existing);
        }
        else
        {
            existing.Story = story;
            existing.DAW = daw;
            existing.VocalChain = vocalChain;
            existing.PromptNotes = promptNotes;
            existing.ProductionNotes = productionNotes;
            existing.HumanContributionNotes = humanContributionNotes;
            existing.YoutubeUrl = youtubeUrl;
            existing.ToolsUsed = toolsJson;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return MapProcess(existing);
    }

    public async Task DeleteCreationProcessAsync(Guid trackId)
    {
        var existing = await _db.TrackCreationProcesses.FindAsync(trackId);
        if (existing is not null)
        {
            _db.TrackCreationProcesses.Remove(existing);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<IReadOnlyList<TrackVideoProofDto>> GetProofVideosAsync(Guid trackId, bool includeHidden)
    {
        var query = _db.TrackVideoProofs.AsNoTracking().Where(v => v.TrackId == trackId);
        if (!includeHidden)
            query = query.Where(v => v.Visibility == "public");

        var videos = await query.OrderBy(v => v.SortOrder).ThenBy(v => v.CreatedAt).ToListAsync();
        return videos.Select(MapProofVideo).ToList();
    }

    public async Task<TrackVideoProofDto?> GetProofVideoAsync(Guid trackId, Guid videoId)
    {
        var entity = await _db.TrackVideoProofs.AsNoTracking()
            .FirstOrDefaultAsync(v => v.TrackId == trackId && v.Id == videoId);
        return entity is null ? null : MapProofVideo(entity);
    }

    public async Task<TrackVideoProofDto> AddProofVideoAsync(
        Guid trackId, string videoType, string url, string? title, string? description, int sortOrder, string visibility)
    {
        var entity = new TrackVideoProof
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            VideoType = videoType,
            Url = url,
            Title = title,
            Description = description,
            SortOrder = sortOrder,
            Visibility = visibility,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.TrackVideoProofs.Add(entity);
        await _db.SaveChangesAsync();
        return MapProofVideo(entity);
    }

    public async Task<TrackVideoProofDto?> UpdateProofVideoAsync(
        Guid trackId, Guid videoId, string? videoType, string? url, string? title, string? description, int? sortOrder, string? visibility)
    {
        var existing = await _db.TrackVideoProofs.FirstOrDefaultAsync(v => v.TrackId == trackId && v.Id == videoId);
        if (existing is null) return null;

        if (videoType is not null) existing.VideoType = videoType;
        if (url is not null) existing.Url = url;
        if (title is not null) existing.Title = title.Length == 0 ? null : title;
        if (description is not null) existing.Description = description.Length == 0 ? null : description;
        if (sortOrder.HasValue) existing.SortOrder = sortOrder.Value;
        if (visibility is not null) existing.Visibility = visibility;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return MapProofVideo(existing);
    }

    public async Task<bool> DeleteProofVideoAsync(Guid trackId, Guid videoId)
    {
        var existing = await _db.TrackVideoProofs.FirstOrDefaultAsync(v => v.TrackId == trackId && v.Id == videoId);
        if (existing is null) return false;

        _db.TrackVideoProofs.Remove(existing);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> GetNextProofVideoSortOrderAsync(Guid trackId)
    {
        var max = await _db.TrackVideoProofs.Where(v => v.TrackId == trackId)
            .Select(v => (int?)v.SortOrder)
            .MaxAsync();
        return (max ?? -1) + 1;
    }

    private static TrackLyricsDto MapLyrics(TrackLyrics l) => new()
    {
        TrackId = l.TrackId.ToString(),
        Lyrics = l.Lyrics,
        Language = l.Language,
        IsExplicit = l.IsExplicit,
        CreatedAt = l.CreatedAt,
        UpdatedAt = l.UpdatedAt,
    };

    private static BehindTheTrackDto MapProcess(TrackCreationProcess p)
    {
        IReadOnlyList<string> tools = Array.Empty<string>();
        if (!string.IsNullOrEmpty(p.ToolsUsed))
        {
            try { tools = JsonSerializer.Deserialize<List<string>>(p.ToolsUsed) ?? new List<string>(); }
            catch { /* ignore malformed JSON */ }
        }

        return new BehindTheTrackDto
        {
            TrackId = p.TrackId.ToString(),
            Story = p.Story,
            DAW = p.DAW,
            VocalChain = p.VocalChain,
            PromptNotes = p.PromptNotes,
            ProductionNotes = p.ProductionNotes,
            HumanContributionNotes = p.HumanContributionNotes,
            YoutubeUrl = p.YoutubeUrl,
            ToolsUsed = tools,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
        };
    }

    private static TrackVideoProofDto MapProofVideo(TrackVideoProof v) => new()
    {
        Id = v.Id.ToString(),
        TrackId = v.TrackId.ToString(),
        VideoType = v.VideoType,
        Url = v.Url,
        Title = v.Title,
        Description = v.Description,
        SortOrder = v.SortOrder,
        Visibility = v.Visibility,
        CreatedAt = v.CreatedAt,
        UpdatedAt = v.UpdatedAt,
    };
}
