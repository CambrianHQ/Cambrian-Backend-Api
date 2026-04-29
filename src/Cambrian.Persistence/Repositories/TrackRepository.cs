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
                includeTaxonomyColumns: true,
                includeTagSearch: _db.Database.IsRelational());

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
                includeTaxonomyColumns: false,
                includeTagSearch: _db.Database.IsRelational());

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
                .FirstOrDefaultAsync(t => t.Id == id && t.Status != "removed");
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            return await BuildLegacyCompatibleTrackQuery()
                .FirstOrDefaultAsync(t => t.Id == id && t.Status != "removed");
        }
    }

    public async Task<Track?> GetByCambrianTrackIdAsync(string cambrianTrackId)
    {
        try
        {
            return await BuildTrackQuery()
                .FirstOrDefaultAsync(t => t.CambrianTrackId == cambrianTrackId && t.Status != "removed");
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            return await BuildLegacyCompatibleTrackQuery()
                .FirstOrDefaultAsync(t => t.CambrianTrackId == cambrianTrackId && t.Status != "removed");
        }
    }

    public async Task<List<Track>> GetByCreatorIdAsync(string creatorId, Guid? creatorUuid = null)
    {
        try
        {
            return await BuildTrackQuery()
                .Where(t => (t.CreatorId == creatorId || (creatorUuid != null && t.CreatorUuid == creatorUuid))
                    && t.Status != "removed")
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            return await BuildLegacyCompatibleTrackQuery()
                .Where(t => (t.CreatorId == creatorId || (creatorUuid != null && t.CreatorUuid == creatorUuid))
                    && t.Status != "removed")
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
                includeTaxonomyColumns: true,
                includeTagSearch: _db.Database.IsRelational());
            return await query.CountAsync();
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            var query = ApplyBrowseFilters(
                BuildLegacyCompatibleTrackQuery().Where(t => !t.ExclusiveSold && t.Status != "copyright_transferred" && t.Visibility == "public"),
                genre, search, mood, tempo, instrumental, duration,
                includeTaxonomyColumns: false,
                includeTagSearch: _db.Database.IsRelational());
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
        bool includeTaxonomyColumns,
        bool includeTagSearch)
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
        {
            // Match against title, description, genre/subgenre/primaryGenre, mood,
            // creator nav (Creator = legacy ApplicationUser, CreatorEntity =
            // canonical Creator row), and tags. Tags is stored as a comma-joined
            // string via a value converter; access via (string)(object)t.Tags lets
            // EF resolve the store type (string) through the normal member-access
            // path and generate LOWER("Tags") LIKE '%needle%' in SQL. InMemory keeps
            // the CLR list shape, so tag search is enabled only for relational providers.
            var needle = search.ToLower();
            if (includeTaxonomyColumns)
            {
                query = includeTagSearch
                    ? query.Where(t =>
                        t.Title.ToLower().Contains(needle) ||
                        (t.Description != null && t.Description.ToLower().Contains(needle)) ||
                        (t.Genre != null && t.Genre.ToLower().Contains(needle)) ||
                        (t.PrimaryGenre != null && t.PrimaryGenre.ToLower().Contains(needle)) ||
                        (t.Subgenre != null && t.Subgenre.ToLower().Contains(needle)) ||
                        (t.Mood != null && t.Mood.ToLower().Contains(needle)) ||
                        ((string)(object)t.Tags).ToLower().Contains(needle) ||
                        (t.CreatorEntity != null && t.CreatorEntity.Username != null && t.CreatorEntity.Username.ToLower().Contains(needle)) ||
                        (t.CreatorEntity != null && t.CreatorEntity.DisplayName != null && t.CreatorEntity.DisplayName.ToLower().Contains(needle)) ||
                        (t.Creator != null && t.Creator.DisplayName != null && t.Creator.DisplayName.ToLower().Contains(needle)))
                    : query.Where(t =>
                        t.Title.ToLower().Contains(needle) ||
                        (t.Description != null && t.Description.ToLower().Contains(needle)) ||
                        (t.Genre != null && t.Genre.ToLower().Contains(needle)) ||
                        (t.PrimaryGenre != null && t.PrimaryGenre.ToLower().Contains(needle)) ||
                        (t.Subgenre != null && t.Subgenre.ToLower().Contains(needle)) ||
                        (t.Mood != null && t.Mood.ToLower().Contains(needle)) ||
                        (t.CreatorEntity != null && t.CreatorEntity.Username != null && t.CreatorEntity.Username.ToLower().Contains(needle)) ||
                        (t.CreatorEntity != null && t.CreatorEntity.DisplayName != null && t.CreatorEntity.DisplayName.ToLower().Contains(needle)) ||
                        (t.Creator != null && t.Creator.DisplayName != null && t.Creator.DisplayName.ToLower().Contains(needle)));
            }
            else
            {
                // Fallback path for databases still missing the taxonomy columns.
                query = includeTagSearch
                    ? query.Where(t =>
                        t.Title.ToLower().Contains(needle) ||
                        (t.Description != null && t.Description.ToLower().Contains(needle)) ||
                        (t.Genre != null && t.Genre.ToLower().Contains(needle)) ||
                        (t.Mood != null && t.Mood.ToLower().Contains(needle)) ||
                        ((string)(object)t.Tags).ToLower().Contains(needle) ||
                        (t.CreatorEntity != null && t.CreatorEntity.Username != null && t.CreatorEntity.Username.ToLower().Contains(needle)) ||
                        (t.CreatorEntity != null && t.CreatorEntity.DisplayName != null && t.CreatorEntity.DisplayName.ToLower().Contains(needle)) ||
                        (t.Creator != null && t.Creator.DisplayName != null && t.Creator.DisplayName.ToLower().Contains(needle)))
                    : query.Where(t =>
                        t.Title.ToLower().Contains(needle) ||
                        (t.Description != null && t.Description.ToLower().Contains(needle)) ||
                        (t.Genre != null && t.Genre.ToLower().Contains(needle)) ||
                        (t.Mood != null && t.Mood.ToLower().Contains(needle)) ||
                        (t.CreatorEntity != null && t.CreatorEntity.Username != null && t.CreatorEntity.Username.ToLower().Contains(needle)) ||
                        (t.CreatorEntity != null && t.CreatorEntity.DisplayName != null && t.CreatorEntity.DisplayName.ToLower().Contains(needle)) ||
                        (t.Creator != null && t.Creator.DisplayName != null && t.Creator.DisplayName.ToLower().Contains(needle)));
            }
        }

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
        var missingColumnMessage =
            message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no such column", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no column named", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid column name", StringComparison.OrdinalIgnoreCase);

        var taxonomyColumnMessage =
            message.Contains("PrimaryGenre", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Subgenre", StringComparison.OrdinalIgnoreCase);

        return missingColumnMessage && taxonomyColumnMessage;
    }

    public async Task AddAsync(Track track)
    {
        _db.Tracks.Add(track);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            await InsertLegacyCompatibleTrackAsync(track);
            _db.Entry(track).State = EntityState.Detached;
        }
    }

    public async Task UpdateAsync(Track track)
    {
        var trackedEntry = _db.ChangeTracker
            .Entries<Track>()
            .FirstOrDefault(e => e.Entity.Id == track.Id);

        if (trackedEntry is null)
        {
            var persisted = new Track { Id = track.Id };
            _db.Tracks.Attach(persisted);
            trackedEntry = _db.Entry(persisted);
        }

        if (!ReferenceEquals(trackedEntry.Entity, track))
        {
            trackedEntry.CurrentValues.SetValues(track);
            trackedEntry.Entity.Tags = track.Tags.ToList();
            trackedEntry.Property(t => t.Tags).IsModified = true;
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (Exception ex) when (IsMissingTrackTaxonomyColumn(ex))
        {
            await UpdateLegacyCompatibleTrackAsync(track);
            trackedEntry.State = EntityState.Detached;
        }
    }

    public async Task DeleteAsync(Guid id)
    {
        if (!_db.Database.IsRelational())
        {
            var inMemoryTrack = await _db.Tracks.FindAsync(id);
            if (inMemoryTrack is not null)
            {
                inMemoryTrack.Visibility = "hidden";
                inMemoryTrack.Status = "removed";
                await _db.SaveChangesAsync();
            }

            return;
        }

        var trackedEntry = _db.ChangeTracker
            .Entries<Track>()
            .FirstOrDefault(e => e.Entity.Id == id);

        if (trackedEntry is not null)
        {
            trackedEntry.Entity.Visibility = "hidden";
            trackedEntry.Entity.Status = "removed";
            trackedEntry.State = EntityState.Unchanged;
        }

        // Creator deletes must preserve historical purchase/library references.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Tracks\" SET \"Visibility\" = {"hidden"}, \"Status\" = {"removed"} WHERE \"Id\" = {NormalizeGuid(id)}");
    }

    private async Task InsertLegacyCompatibleTrackAsync(Track track)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Tracks" (
                "Id",
                "CambrianTrackId",
                "Title",
                "Description",
                "Genre",
                "Mood",
                "Tempo",
                "Instrumental",
                "Price",
                "Duration",
                "LicenseType",
                "AudioUrl",
                "CoverArtUrl",
                "NonExclusivePriceCents",
                "ExclusivePriceCents",
                "CopyrightBuyoutPriceCents",
                "ExclusiveSold",
                "Status",
                "CopyrightOwnerId",
                "CopyrightTransferredAt",
                "OriginalCreatorId",
                "Visibility",
                "CreatedAt",
                "CreatorId",
                "CreatorUuid",
                "Tags",
                "UseCase",
                "TrendingScore"
            )
            VALUES (
                {NormalizeGuid(track.Id)},
                {track.CambrianTrackId},
                {track.Title},
                {track.Description},
                {track.Genre},
                {track.Mood},
                {track.Tempo},
                {track.Instrumental},
                {track.Price},
                {track.Duration},
                {track.LicenseType},
                {track.AudioUrl},
                {track.CoverArtUrl},
                {track.NonExclusivePriceCents},
                {track.ExclusivePriceCents},
                {track.CopyrightBuyoutPriceCents},
                {track.ExclusiveSold},
                {track.Status},
                {track.CopyrightOwnerId},
                {track.CopyrightTransferredAt},
                {track.OriginalCreatorId},
                {track.Visibility},
                {track.CreatedAt},
                {track.CreatorId},
                {NormalizeGuid(track.CreatorUuid)},
                {SerializeTags(track.Tags)},
                {track.UseCase},
                {track.TrendingScore}
            )
            """);
    }

    private async Task UpdateLegacyCompatibleTrackAsync(Track track)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Tracks"
            SET
                "CambrianTrackId" = {track.CambrianTrackId},
                "Title" = {track.Title},
                "Description" = {track.Description},
                "Genre" = {track.Genre},
                "Mood" = {track.Mood},
                "Tempo" = {track.Tempo},
                "Instrumental" = {track.Instrumental},
                "Price" = {track.Price},
                "Duration" = {track.Duration},
                "LicenseType" = {track.LicenseType},
                "AudioUrl" = {track.AudioUrl},
                "CoverArtUrl" = {track.CoverArtUrl},
                "NonExclusivePriceCents" = {track.NonExclusivePriceCents},
                "ExclusivePriceCents" = {track.ExclusivePriceCents},
                "CopyrightBuyoutPriceCents" = {track.CopyrightBuyoutPriceCents},
                "ExclusiveSold" = {track.ExclusiveSold},
                "Status" = {track.Status},
                "CopyrightOwnerId" = {track.CopyrightOwnerId},
                "CopyrightTransferredAt" = {track.CopyrightTransferredAt},
                "OriginalCreatorId" = {track.OriginalCreatorId},
                "Visibility" = {track.Visibility},
                "CreatedAt" = {track.CreatedAt},
                "CreatorId" = {track.CreatorId},
                "CreatorUuid" = {NormalizeGuid(track.CreatorUuid)},
                "Tags" = {SerializeTags(track.Tags)},
                "UseCase" = {track.UseCase},
                "TrendingScore" = {track.TrendingScore}
            WHERE "Id" = {NormalizeGuid(track.Id)}
            """);
    }

    private static string SerializeTags(ICollection<string>? tags)
        => string.Join(',', tags ?? []);

    private object? NormalizeGuid(Guid? value)
        => IsSqliteProvider()
            ? value?.ToString()
            : value;

    private object NormalizeGuid(Guid value)
        => IsSqliteProvider()
            ? value.ToString()
            : value;

    private bool IsSqliteProvider()
        => string.Equals(_db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal);

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
