using Cambrian.Application.Interfaces;
using Cambrian.Application.DTOs.Creator;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class TrackRepository : ITrackRepository
{
    private readonly CambrianDbContext _db;

    public TrackRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<List<Track>> BrowseAsync()
    {
        try
        {
            return await BuildTrackQuery()
                .Where(t => !t.ExclusiveSold && t.Status != "copyright_transferred" && t.Visibility == "public")
                .OrderByDescending(t => t.CreatedAt)
                .Take(200)
                .ToListAsync();
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            return await BuildLegacyCompatibleTrackQuery()
                .Where(t => !t.ExclusiveSold && t.Status != "copyright_transferred" && t.Visibility == "public")
                .OrderByDescending(t => t.CreatedAt)
                .Take(200)
                .ToListAsync();
        }
    }

    public async Task<List<Track>> BrowseAsync(int page, int pageSize, string? genre, string? search, string? sort = null)
    {
        return await BrowseAsync(page, pageSize, genre, search, sort, null, null, null, null);
    }

    public async Task<List<Track>> BrowseAsync(int page, int pageSize, string? genre, string? search, string? sort,
        string? mood, string? tempo, bool? instrumental, string? duration)
    {
        try
        {
            var query = ApplyBrowseFilters(
                BuildTrackQuery().Where(t => !t.ExclusiveSold && t.Status != "copyright_transferred" && t.Visibility == "public"),
                genre, search, mood, tempo, instrumental, duration,
                includeTaxonomyColumns: true);

            return await ApplySort(query, sort)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            var query = ApplyBrowseFilters(
                BuildLegacyCompatibleTrackQuery().Where(t => !t.ExclusiveSold && t.Status != "copyright_transferred" && t.Visibility == "public"),
                genre, search, mood, tempo, instrumental, duration,
                includeTaxonomyColumns: false);

            return await ApplySort(query, sort)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }
    }

    public async Task<Track?> GetByIdAsync(Guid id)
    {
        try
        {
            return await BuildTrackQuery()
                .FirstOrDefaultAsync(t => t.Id == id);
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            return await BuildLegacyCompatibleTrackQuery()
                .FirstOrDefaultAsync(t => t.Id == id);
        }
    }

    public async Task<Track?> GetByCambrianTrackIdAsync(string cambrianTrackId)
    {
        try
        {
            return await BuildTrackQuery()
                .FirstOrDefaultAsync(t => t.CambrianTrackId == cambrianTrackId);
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            return await BuildLegacyCompatibleTrackQuery()
                .FirstOrDefaultAsync(t => t.CambrianTrackId == cambrianTrackId);
        }
    }

    public async Task<List<Track>> GetByCreatorIdAsync(string creatorId, Guid? creatorUuid = null)
    {
        try
        {
            return await BuildTrackQuery()
                .Where(t => t.CreatorId == creatorId || (creatorUuid != null && t.CreatorUuid == creatorUuid))
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            return await BuildLegacyCompatibleTrackQuery()
                .Where(t => t.CreatorId == creatorId || (creatorUuid != null && t.CreatorUuid == creatorUuid))
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }
    }

    public async Task<List<CreatorDashboardTrackSummary>> GetDashboardTrackSummariesAsync(string creatorId, Guid? creatorUuid = null)
    {
        return await _db.Tracks
            .Where(t => t.CreatorId == creatorId || (creatorUuid != null && t.CreatorUuid == creatorUuid))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new CreatorDashboardTrackSummary
            {
                Id = t.Id,
                Title = t.Title,
                CoverArtUrl = t.CoverArtUrl,
            })
            .ToListAsync();
    }

    public async Task<List<CreatorTrackSummary>> GetCreatorTrackSummariesAsync(string creatorId, Guid? creatorUuid = null)
    {
        return await _db.Tracks
            .Where(t => t.CreatorId == creatorId || (creatorUuid != null && t.CreatorUuid == creatorUuid))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new CreatorTrackSummary
            {
                Id = t.Id,
                CambrianTrackId = t.CambrianTrackId,
                Title = t.Title,
                Description = t.Description,
                Genre = t.Genre,
                Mood = t.Mood,
                Tempo = t.Tempo,
                Tags = t.Tags.ToList(),
                Instrumental = t.Instrumental,
                Visibility = t.Visibility,
                Price = t.Price,
                NonExclusivePriceCents = t.NonExclusivePriceCents,
                ExclusivePriceCents = t.ExclusivePriceCents,
                CopyrightBuyoutPriceCents = t.CopyrightBuyoutPriceCents,
                ExclusiveSold = t.ExclusiveSold,
                Status = t.Status,
                LicenseType = t.LicenseType,
                Duration = t.Duration,
                AudioUrl = t.AudioUrl,
                CoverArtUrl = t.CoverArtUrl,
                CreatedAt = t.CreatedAt,
            })
            .ToListAsync();
    }

    public async Task<List<Track>> GetStorefrontTracksAsync(string creatorId, Guid? creatorUuid = null)
    {
        try
        {
            return await BuildTrackQuery()
                .Where(t => (t.CreatorId == creatorId || (creatorUuid != null && t.CreatorUuid == creatorUuid))
                    && t.Visibility == "public"
                    && t.Status != "copyright_transferred"
                    && !t.ExclusiveSold)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            return await BuildLegacyCompatibleTrackQuery()
                .Where(t => (t.CreatorId == creatorId || (creatorUuid != null && t.CreatorUuid == creatorUuid))
                    && t.Visibility == "public"
                    && t.Status != "copyright_transferred"
                    && !t.ExclusiveSold)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }
    }

    public async Task<int> CountAsync(string? genre = null, string? search = null,
        string? mood = null, string? tempo = null, bool? instrumental = null, string? duration = null)
    {
        try
        {
            var query = ApplyBrowseFilters(
                _db.Tracks.Where(t => !t.ExclusiveSold && t.Status != "copyright_transferred" && t.Visibility == "public"),
                genre, search, mood, tempo, instrumental, duration,
                includeTaxonomyColumns: true);
            return await query.CountAsync();
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            var query = ApplyBrowseFilters(
                BuildLegacyCompatibleTrackQuery().Where(t => !t.ExclusiveSold && t.Status != "copyright_transferred" && t.Visibility == "public"),
                genre, search, mood, tempo, instrumental, duration,
                includeTaxonomyColumns: false);
            return await query.CountAsync();
        }
    }

    private IQueryable<Track> BuildTrackQuery()
        => _db.Tracks
            .Include(t => t.Creator)
            .Include(t => t.CreatorEntity);

    // Compatibility path for databases that have not yet applied the taxonomy migration.
    // By projecting only legacy columns we avoid selecting PrimaryGenre/Subgenre at SQL time.
    private IQueryable<Track> BuildLegacyCompatibleTrackQuery()
        => _db.Tracks
            .AsNoTracking()
            .Select(t => new Track
            {
                Id = t.Id,
                CambrianTrackId = t.CambrianTrackId,
                Title = t.Title,
                Description = t.Description,
                Genre = t.Genre,
                Mood = t.Mood,
                Tempo = t.Tempo,
                Instrumental = t.Instrumental,
                Price = t.Price,
                Duration = t.Duration,
                LicenseType = t.LicenseType,
                AudioUrl = t.AudioUrl,
                CoverArtUrl = t.CoverArtUrl,
                NonExclusivePriceCents = t.NonExclusivePriceCents,
                ExclusivePriceCents = t.ExclusivePriceCents,
                CopyrightBuyoutPriceCents = t.CopyrightBuyoutPriceCents,
                ExclusiveSold = t.ExclusiveSold,
                Status = t.Status,
                CopyrightOwnerId = t.CopyrightOwnerId,
                CopyrightTransferredAt = t.CopyrightTransferredAt,
                OriginalCreatorId = t.OriginalCreatorId,
                Visibility = t.Visibility,
                CreatedAt = t.CreatedAt,
                CreatorId = t.CreatorId,
                CreatorUuid = t.CreatorUuid,
                Tags = t.Tags,
                UseCase = t.UseCase,
                TrendingScore = t.TrendingScore,
                Creator = t.Creator == null
                    ? null!
                    : new ApplicationUser
                    {
                        Id = t.Creator.Id,
                        UserName = t.Creator.UserName,
                        DisplayName = t.Creator.DisplayName,
                        ProfileImageUrl = t.Creator.ProfileImageUrl,
                        CreatorTier = t.Creator.CreatorTier
                    },
                CreatorEntity = t.CreatorEntity == null
                    ? null
                    : new Creator
                    {
                        Id = t.CreatorEntity.Id,
                        UserId = t.CreatorEntity.UserId,
                        Username = t.CreatorEntity.Username,
                        DisplayName = t.CreatorEntity.DisplayName,
                        ProfileImageUrl = t.CreatorEntity.ProfileImageUrl
                    }
            });

    private static IQueryable<Track> ApplyBrowseFilters(
        IQueryable<Track> query,
        string? genre,
        string? search,
        string? mood,
        string? tempo,
        bool? instrumental,
        string? duration,
        bool includeTaxonomyColumns)
    {
        if (!string.IsNullOrWhiteSpace(genre))
        {
            var normalizedGenre = genre.ToLower();
            query = includeTaxonomyColumns
                ? query.Where(t =>
                    (t.Subgenre != null && t.Subgenre.ToLower() == normalizedGenre) ||
                    (t.PrimaryGenre != null && t.PrimaryGenre.ToLower() == normalizedGenre) ||
                    (t.Genre != null && t.Genre.ToLower() == normalizedGenre))
                : query.Where(t => t.Genre != null && t.Genre.ToLower() == normalizedGenre);
        }

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t => t.Title.ToLower().Contains(search.ToLower()));

        if (!string.IsNullOrWhiteSpace(mood))
            query = query.Where(t => t.Mood != null && t.Mood.ToLower() == mood.ToLower());

        if (!string.IsNullOrWhiteSpace(tempo))
            query = query.Where(t => t.Tempo != null && t.Tempo.ToLower() == tempo.ToLower());

        if (instrumental.HasValue)
            query = query.Where(t => t.Instrumental == instrumental.Value);

        if (!string.IsNullOrWhiteSpace(duration))
            query = query.Where(t => t.Duration != null && t.Duration.ToLower() == duration.ToLower());

        return query;
    }

    private static IQueryable<Track> ApplySort(IQueryable<Track> query, string? sort)
        => sort?.ToLower() switch
        {
            "price" => query.OrderBy(t => t.Price),
            "price_desc" => query.OrderByDescending(t => t.Price),
            "title" => query.OrderBy(t => t.Title),
            _ => query.OrderByDescending(t => t.CreatedAt)
        };

    private static bool IsMissingTrackTaxonomyColumn(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) &&
               (message.Contains("PrimaryGenre", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Subgenre", StringComparison.OrdinalIgnoreCase));
    }

    public async Task AddAsync(Track track)
    {
        _db.Tracks.Add(track);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Track track)
    {
        _db.Tracks.Update(track);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var track = await _db.Tracks.FindAsync(id);

        if (track is not null)
        {
            _db.Tracks.Remove(track);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> TryMarkExclusiveSoldAsync(Guid trackId)
    {
        // Atomic UPDATE with WHERE clause — only succeeds if ExclusiveSold is currently false.
        // Prevents race conditions on concurrent exclusive purchase attempts.
        var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Tracks\" SET \"ExclusiveSold\" = true WHERE \"Id\" = {trackId} AND \"ExclusiveSold\" = false");
        return affected > 0;
    }

    public async Task<bool> TryMarkCopyrightBuyoutAsync(Guid trackId, string buyerUserId)
    {
        // Atomic UPDATE with multi-field WHERE clause — sets all six fields in a single
        // conditional statement. Prevents race conditions on concurrent copyright buyout attempts.
        var now = DateTime.UtcNow;
        var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Tracks\" SET \"ExclusiveSold\" = true, \"Status\" = 'copyright_transferred', \"Visibility\" = 'hidden', \"OriginalCreatorId\" = \"CreatorId\", \"CopyrightOwnerId\" = {buyerUserId}, \"CopyrightTransferredAt\" = {now} WHERE \"Id\" = {trackId} AND \"ExclusiveSold\" = false AND \"Status\" != 'copyright_transferred'");
        return affected > 0;
    }
}
