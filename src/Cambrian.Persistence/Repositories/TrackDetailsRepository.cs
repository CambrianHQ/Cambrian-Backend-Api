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

    public async Task<BehindTheTrackDto> UpsertCreationProcessAsync(Guid trackId, string? story, string? youtubeUrl, IReadOnlyList<string> toolsUsed)
    {
        var toolsJson = toolsUsed.Count > 0 ? JsonSerializer.Serialize(toolsUsed) : null;
        var existing = await _db.TrackCreationProcesses.FindAsync(trackId);
        if (existing is null)
        {
            existing = new TrackCreationProcess
            {
                TrackId = trackId,
                Story = story,
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
            YoutubeUrl = p.YoutubeUrl,
            ToolsUsed = tools,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
        };
    }
}
