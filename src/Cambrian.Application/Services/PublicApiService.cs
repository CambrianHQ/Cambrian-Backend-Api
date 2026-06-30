using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.DTOs.Public;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

/// <summary>
/// Implements the public, read-only discovery surface for the MCP server. Reuses the
/// existing catalog/profile services for live, real metrics and the public directory
/// repository for aggregates, then maps everything to public-safe DTOs with canonical
/// URLs and SEO metadata. Nothing here surfaces drafts/hidden content, storage keys,
/// emails, Stripe/payment data, fee breakdowns, earnings, or admin fields.
/// </summary>
public sealed class PublicApiService : IPublicApiService
{
    /// <summary>Hard upper bound on any public page size.</summary>
    public const int MaxPageSize = 50;

    /// <summary>Default page size when the caller does not specify one.</summary>
    public const int DefaultPageSize = 20;

    private const int RecentTracksLimit = 12;
    private const int FeaturedCreatorsMax = 24;
    private const int RelatedGenresMax = 8;
    private const int MaxSitemapTracks = 5000;
    private const int MaxSitemapCreators = 2000;
    private const int MetaDescriptionMax = 160;

    private static readonly string[] StaticSitemapPaths =
    {
        "", "tracks", "creators", "genres", "trending", "new",
        "pricing", "faq", "release-ready", "authorship", "creator-guide"
    };

    private readonly ICatalogService _catalog;
    private readonly ICreatorProfileRepository _profiles;
    private readonly IPublicDirectoryRepository _directory;
    private readonly IPublicUrlResolver _urls;

    public PublicApiService(
        ICatalogService catalog,
        ICreatorProfileRepository profiles,
        IPublicDirectoryRepository directory,
        IPublicUrlResolver urls)
    {
        _catalog = catalog;
        _profiles = profiles;
        _directory = directory;
        _urls = urls;
    }

    // ── Tracks ───────────────────────────────────────────────────────────────

    public async Task<PublicListResponse<PublicTrackDto>> SearchTracksAsync(
        string? query, string? genre, string? mood, string? tempo, bool? instrumental,
        string? sort, int page, int pageSize)
    {
        page = NormalizePage(page);
        pageSize = NormalizePageSize(pageSize);

        var paged = await _catalog.GetCatalogPagedAsync(
            page, pageSize, genre, query, sort, mood, tempo, instrumental, duration: null);

        return BuildTrackList(paged, "tracks",
            "Browse music tracks on Cambrian",
            "Search Cambrian's catalogue of music tracks with verifiable provenance.");
    }

    public async Task<PublicTrackDto?> GetTrackAsync(string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return null;

        var track = await _catalog.GetTrackAsync(trackId.Trim());
        // Public detail only — exclude hidden/limited/draft and copyright-transferred tracks.
        if (track is null || !string.Equals(track.Visibility, "public", StringComparison.OrdinalIgnoreCase))
            return null;

        return MapTrack(track);
    }

    public async Task<PublicListResponse<PublicTrackDto>> GetTrendingAsync(
        int page, int pageSize, string? genre, string? mood, string? tempo, bool? instrumental)
    {
        page = NormalizePage(page);
        pageSize = NormalizePageSize(pageSize);

        var paged = await _catalog.GetTrendingPagedAsync(page, pageSize, genre, mood, tempo, instrumental, duration: null);

        return BuildTrackList(paged, "trending",
            "Trending music on Cambrian",
            "The most-played tracks on Cambrian right now.");
    }

    public async Task<PublicListResponse<PublicTrackDto>> GetLatestAsync(int page, int pageSize)
    {
        page = NormalizePage(page);
        pageSize = NormalizePageSize(pageSize);

        var paged = await _catalog.GetCatalogPagedAsync(
            page, pageSize, genre: null, search: null, sort: "newest",
            mood: null, tempo: null, instrumental: null, duration: null);

        return BuildTrackList(paged, "new",
            "Latest releases on Cambrian",
            "The newest tracks published on Cambrian.");
    }

    // ── Creators ─────────────────────────────────────────────────────────────

    public async Task<PublicListResponse<PublicCreatorSummaryDto>> SearchCreatorsAsync(string? query, int page, int pageSize)
    {
        page = NormalizePage(page);
        pageSize = NormalizePageSize(pageSize);

        var result = await _directory.SearchCreatorsAsync(query, page, pageSize);

        return new PublicListResponse<PublicCreatorSummaryDto>
        {
            Items = result.Rows.Select(MapCreatorSummary).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = result.TotalCount,
            CanonicalUrl = _urls.SiteUrl("creators"),
            MetaTitle = "Creators on Cambrian",
            MetaDescription = "Discover music creators on Cambrian.",
        };
    }

    public async Task<PublicCreatorDto?> GetCreatorAsync(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var profile = await _profiles.GetBySlugAsync(slug.Trim());
        if (profile is null)
            return null;

        var tracks = await _catalog.GetByCreatorPagedAsync(profile.UserId, creatorUuid: null, page: 1, pageSize: RecentTracksLimit);
        return MapCreatorProfile(profile, tracks);
    }

    public async Task<IReadOnlyList<PublicCreatorSummaryDto>> GetFeaturedCreatorsAsync(int limit)
    {
        limit = Math.Clamp(limit, 1, FeaturedCreatorsMax);
        var rows = await _directory.GetFeaturedCreatorsAsync(limit);
        return rows.Select(MapCreatorSummary).ToList();
    }

    // ── Genres ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PublicGenreDto>> GetGenresAsync()
    {
        var counts = await _directory.GetGenreCountsAsync();
        return counts.Select(MapGenre).ToList();
    }

    public async Task<PublicGenreDetailDto?> GetGenreAsync(string genre, int page, int pageSize)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return null;

        page = NormalizePage(page);
        pageSize = NormalizePageSize(pageSize);

        var counts = await _directory.GetGenreCountsAsync();
        var match = counts.FirstOrDefault(g =>
            string.Equals(g.Name, genre, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_urls.Slugify(g.Name), _urls.Slugify(genre), StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return null;

        var paged = await _catalog.GetCatalogPagedAsync(
            page, pageSize, genre: match.Name, search: null, sort: "newest",
            mood: null, tempo: null, instrumental: null, duration: null);

        var slug = _urls.Slugify(match.Name);
        var related = counts
            .Where(g => !string.Equals(g.Name, match.Name, StringComparison.OrdinalIgnoreCase))
            .Take(RelatedGenresMax)
            .Select(MapGenre)
            .ToList();

        return new PublicGenreDetailDto
        {
            Name = match.Name,
            Slug = slug,
            TrackCount = match.TrackCount,
            Tracks = BuildTrackList(paged, $"genres/{slug}",
                $"{match.Name} music on Cambrian",
                $"Browse {match.TrackCount} {match.Name} tracks on Cambrian."),
            RelatedGenres = related,
            Title = match.Name,
            Description = $"{match.Name} music on Cambrian.",
            CanonicalUrl = _urls.GenreUrl(slug),
            UpdatedAt = paged.Items.Count > 0 ? paged.Items.Max(t => t.CreatedAt) : null,
            Tags = new List<string> { match.Name },
            MetaTitle = $"{match.Name} music | Cambrian",
            MetaDescription = $"Discover {match.TrackCount} {match.Name} tracks on Cambrian.",
            StructuredDataType = "CollectionPage",
        };
    }

    // ── Platform ─────────────────────────────────────────────────────────────

    public async Task<PublicPlatformStatsDto> GetPlatformStatsAsync()
    {
        var counts = await _directory.GetPlatformCountsAsync();
        return new PublicPlatformStatsDto
        {
            TrackCount = counts.TrackCount,
            CreatorCount = counts.CreatorCount,
            GenreCount = counts.GenreCount,
            TotalPlays = counts.TotalPlays,
            Title = "Cambrian platform statistics",
            Description = $"{counts.TrackCount} tracks from {counts.CreatorCount} creators across {counts.GenreCount} genres.",
            CanonicalUrl = _urls.SiteUrl("about"),
            MetaTitle = "Cambrian by the numbers",
            MetaDescription = $"{counts.TrackCount} tracks, {counts.CreatorCount} creators, {counts.TotalPlays} plays on Cambrian.",
            StructuredDataType = "Dataset",
        };
    }

    public Task<PublicPricingDto> GetPricingAsync()
    {
        var tiers = TierManifest.All.Select(t => new PublicPricingTierDto
        {
            Slug = t.Slug,
            Name = t.DisplayName,
            PriceCentsMonthly = t.PriceCents,
            PriceMonthly = t.PriceCents / 100m,
            BillingPeriod = "month",
            IsFree = t.PriceCents == 0,
            UploadLimit = t.UploadLimit,
            PlatformFeePercent = Math.Round(t.FeeRate * 100m, 2),
            Features = t.Features.ToList(),
        }).ToList();

        var dto = new PublicPricingDto
        {
            Currency = "USD",
            Tiers = tiers,
            Title = "Cambrian pricing",
            Description = "Free, Creator, and Pro plans for music creators.",
            CanonicalUrl = _urls.SiteUrl("pricing"),
            MetaTitle = "Pricing | Cambrian",
            MetaDescription = "Compare Cambrian's Free, Creator, and Pro plans.",
            StructuredDataType = "OfferCatalog",
            RelatedUrls = new List<string> { _urls.SiteUrl("faq") },
        };
        return Task.FromResult(dto);
    }

    public Task<PublicFaqDto> GetFaqAsync()
    {
        var dto = new PublicFaqDto
        {
            Items = PublicContentCatalog.Faq().ToList(),
            Title = "Frequently Asked Questions",
            Description = "Answers to common questions about Cambrian.",
            CanonicalUrl = _urls.SiteUrl("faq"),
            UpdatedAt = PublicContentCatalog.LastModified,
            MetaTitle = "FAQ | Cambrian",
            MetaDescription = "Frequently asked questions about Cambrian, provenance, Release Ready, and pricing.",
            StructuredDataType = "FAQPage",
            RelatedUrls = new List<string> { _urls.SiteUrl("pricing"), _urls.SiteUrl("creator-guide") },
        };
        return Task.FromResult(dto);
    }

    public async Task<PublicSitemapDto> GetSitemapAsync()
    {
        var data = await _directory.GetSitemapDataAsync(MaxSitemapTracks, MaxSitemapCreators);

        var entries = new List<PublicSitemapEntryDto>();

        foreach (var path in StaticSitemapPaths)
        {
            entries.Add(new PublicSitemapEntryDto
            {
                Url = _urls.SiteUrl(path),
                ChangeFrequency = path == "" ? "daily" : "weekly",
                Priority = path == "" ? 1.0 : 0.6,
                Type = "page",
            });
        }

        foreach (var track in data.Tracks)
        {
            entries.Add(new PublicSitemapEntryDto
            {
                Url = _urls.TrackUrl(track.CambrianTrackId),
                LastModified = track.LastModified,
                ChangeFrequency = "monthly",
                Priority = 0.5,
                Type = "track",
            });
        }

        foreach (var creator in data.Creators)
        {
            entries.Add(new PublicSitemapEntryDto
            {
                Url = _urls.CreatorUrl(creator.Slug),
                LastModified = creator.LastModified,
                ChangeFrequency = "weekly",
                Priority = 0.7,
                Type = "creator",
            });
        }

        return new PublicSitemapDto { Entries = entries, Count = entries.Count };
    }

    // ── Content pages ────────────────────────────────────────────────────────

    public Task<PublicContentPageDto> GetReleaseReadyAsync() =>
        Task.FromResult(FinishContentPage(PublicContentCatalog.ReleaseReady()));

    public Task<PublicContentPageDto> GetAuthorshipAsync() =>
        Task.FromResult(FinishContentPage(PublicContentCatalog.Authorship()));

    public Task<PublicContentPageDto> GetCreatorGuideAsync() =>
        Task.FromResult(FinishContentPage(PublicContentCatalog.CreatorGuide()));

    private PublicContentPageDto FinishContentPage(PublicContentPageDto page)
    {
        page.CanonicalUrl = _urls.SiteUrl(page.Slug);
        page.UpdatedAt = PublicContentCatalog.LastModified;
        page.MetaTitle = $"{page.Title} | Cambrian";
        page.MetaDescription = Truncate(page.Summary);
        page.StructuredDataType = "Article";
        page.RelatedUrls = new List<string> { _urls.SiteUrl("pricing"), _urls.SiteUrl("faq") };
        return page;
    }

    // ── Mapping ──────────────────────────────────────────────────────────────

    private PublicListResponse<PublicTrackDto> BuildTrackList(
        PagedResult<TrackResponse> paged, string canonicalPath, string metaTitle, string metaDescription) =>
        new()
        {
            Items = paged.Items.Select(MapTrack).ToList(),
            Page = paged.Page,
            PageSize = paged.PageSize,
            TotalCount = paged.TotalCount,
            CanonicalUrl = _urls.SiteUrl(canonicalPath),
            MetaTitle = metaTitle,
            MetaDescription = metaDescription,
        };

    private PublicTrackDto MapTrack(TrackResponse t)
    {
        var trackId = !string.IsNullOrWhiteSpace(t.CambrianTrackId) ? t.CambrianTrackId : t.Id;
        var creator = MapCreatorRef(t);

        var dto = new PublicTrackDto
        {
            Id = t.Id,
            TrackId = t.CambrianTrackId,
            Genre = string.IsNullOrWhiteSpace(t.Genre) ? null : t.Genre,
            PrimaryGenre = t.PrimaryGenre,
            Subgenre = t.Subgenre,
            Mood = t.Mood,
            Tempo = t.Tempo,
            Instrumental = t.Instrumental,
            Duration = t.Duration,
            Price = t.NonExclusivePrice,
            PriceCents = (int)Math.Round(t.NonExclusivePrice * 100m, MidpointRounding.AwayFromZero),
            Currency = "USD",
            StreamAvailable = t.StreamAvailable,
            AudioPreviewUrl = t.StreamAvailable ? _urls.AudioStreamUrl(t.Id) : null,
            Creator = creator,
            Plays = t.Plays,
            Sales = t.Sales,
            AiGenerated = t.AiGenerated,
            ProvenanceStatus = string.IsNullOrWhiteSpace(t.ProvenanceStatus) ? "none" : t.ProvenanceStatus,
            CreatedAt = t.CreatedAt,
            // SEO base
            Title = t.Title,
            Description = t.Description,
            CanonicalUrl = _urls.TrackUrl(trackId),
            ImageUrl = _urls.ImageUrl(t.CoverArtUrl),
            UpdatedAt = t.CreatedAt,
            Tags = t.Tags?.ToList() ?? new List<string>(),
            MetaTitle = $"{t.Title} by {creator?.DisplayName ?? "Unknown Artist"} | Cambrian",
            MetaDescription = BuildTrackMeta(t, creator),
            StructuredDataType = "MusicRecording",
        };

        if (creator is not null && !string.IsNullOrEmpty(creator.CanonicalUrl))
            dto.RelatedUrls.Add(creator.CanonicalUrl);
        if (!string.IsNullOrWhiteSpace(t.Genre))
            dto.RelatedUrls.Add(_urls.GenreUrl(_urls.Slugify(t.Genre)));

        return dto;
    }

    private PublicCreatorRef MapCreatorRef(TrackResponse t)
    {
        var name = string.IsNullOrWhiteSpace(t.Artist) ? "Unknown Artist" : t.Artist!;
        var canonical = string.IsNullOrWhiteSpace(t.CreatorSlug)
            ? _urls.SiteUrl("creators")
            : _urls.CreatorUrl(t.CreatorSlug!);

        return new PublicCreatorRef
        {
            Slug = t.CreatorSlug,
            DisplayName = name,
            CanonicalUrl = canonical,
            ImageUrl = _urls.ImageUrl(t.CreatorProfileImageUrl),
        };
    }

    private PublicCreatorSummaryDto MapCreatorSummary(PublicCreatorRow row)
    {
        var description = string.IsNullOrWhiteSpace(row.Bio)
            ? $"{row.DisplayName} on Cambrian"
            : Truncate(row.Bio!);

        return new PublicCreatorSummaryDto
        {
            Id = row.Id,
            Slug = row.Slug,
            Username = row.Username,
            DisplayName = row.DisplayName,
            Niche = row.Niche,
            TrackCount = row.TrackCount,
            Title = row.DisplayName,
            Description = description,
            CanonicalUrl = _urls.CreatorUrl(row.Slug),
            ImageUrl = _urls.ImageUrl(row.ImageUrl),
            UpdatedAt = row.UpdatedAt,
            Tags = string.IsNullOrWhiteSpace(row.Niche) ? new List<string>() : new List<string> { row.Niche! },
            MetaTitle = $"{row.DisplayName} | Cambrian",
            MetaDescription = description,
            StructuredDataType = "MusicGroup",
        };
    }

    private PublicCreatorDto MapCreatorProfile(CreatorProfileDto profile, PagedResult<TrackResponse> tracks)
    {
        var name = !string.IsNullOrWhiteSpace(profile.DisplayName)
            ? profile.DisplayName!
            : (profile.Username ?? profile.Slug);
        var description = string.IsNullOrWhiteSpace(profile.Bio)
            ? $"{name} on Cambrian"
            : Truncate(profile.Bio);

        var dto = new PublicCreatorDto
        {
            Id = profile.Id,
            Slug = profile.Slug,
            Username = profile.Username,
            DisplayName = name,
            Bio = string.IsNullOrWhiteSpace(profile.Bio) ? null : profile.Bio,
            Niche = profile.Niche,
            SocialLinks = (profile.SocialLinks ?? new List<SocialLinkDto>())
                .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                .Select(s => new PublicSocialLinkDto { Platform = s.Platform, Url = s.Url })
                .ToList(),
            Stats = new PublicCreatorStatsDto
            {
                // Earnings/revenue intentionally excluded — only real, non-financial metrics.
                Plays = profile.Stats.TotalPlays,
                Followers = profile.Stats.FollowerCount,
                Sales = profile.Stats.TotalDownloads,
                TrackCount = tracks.TotalCount,
            },
            TrackCount = tracks.TotalCount,
            RecentTracks = tracks.Items.Select(MapTrack).ToList(),
            Title = name,
            Description = description,
            CanonicalUrl = _urls.CreatorUrl(profile.Slug),
            ImageUrl = _urls.ImageUrl(profile.ProfileImageUrl),
            UpdatedAt = profile.UpdatedAt,
            Tags = string.IsNullOrWhiteSpace(profile.Niche) ? new List<string>() : new List<string> { profile.Niche! },
            MetaTitle = $"{name} | Cambrian",
            MetaDescription = description,
            StructuredDataType = "MusicGroup",
        };

        return dto;
    }

    private PublicGenreDto MapGenre(PublicGenreCount g)
    {
        var slug = _urls.Slugify(g.Name);
        return new PublicGenreDto
        {
            Name = g.Name,
            Slug = slug,
            TrackCount = g.TrackCount,
            Title = g.Name,
            Description = $"{g.TrackCount} {g.Name} tracks on Cambrian.",
            CanonicalUrl = _urls.GenreUrl(slug),
            Tags = new List<string> { g.Name },
            MetaTitle = $"{g.Name} music | Cambrian",
            MetaDescription = $"Browse {g.TrackCount} {g.Name} tracks on Cambrian.",
            StructuredDataType = "CollectionPage",
        };
    }

    private static string BuildTrackMeta(TrackResponse t, PublicCreatorRef? creator)
    {
        var artist = creator?.DisplayName ?? "Unknown Artist";
        var descriptors = new List<string>();
        if (!string.IsNullOrWhiteSpace(t.Genre)) descriptors.Add(t.Genre);
        if (!string.IsNullOrWhiteSpace(t.Mood)) descriptors.Add(t.Mood!);
        var descriptor = descriptors.Count > 0 ? $" ({string.Join(", ", descriptors)})" : "";
        return Truncate($"Listen to \"{t.Title}\"{descriptor} by {artist} on Cambrian.");
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= MetaDescriptionMax ? trimmed : trimmed[..(MetaDescriptionMax - 1)].TrimEnd() + "…";
    }

    private static int NormalizePage(int page) => page < 1 ? 1 : page;

    private static int NormalizePageSize(int pageSize) =>
        pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
}
