using Cambrian.Application.AI.Discovery.Dtos;
using Cambrian.Application.AI.Discovery.Queries;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.AI.Discovery.Builders;

/// <summary>
/// Builds the AI-optimised response shapes from domain entities.
/// Pure mapping — no I/O, no business rules.
/// </summary>
public static class TrackAiResponseBuilder
{
    public static TrackSearchResultDto Build(
        Track track,
        double score,
        SearchTracksQuery query,
        decimal feeRate,
        ApplicationUser? creator,
        string? slug,
        string? profileImageUrl)
    {
        var confidence = score switch
        {
            >= 0.7 => "high",
            >= 0.4 => "medium",
            _ => "low"
        };

        var bestUseCase = !string.IsNullOrEmpty(track.UseCase)
            ? track.UseCase
            : query.UseCase ?? "general";

        return new TrackSearchResultDto
        {
            TrackId = track.CambrianTrackId,
            Title = track.Title,
            Creator = BuildCreatorSummary(track, creator, slug, profileImageUrl),
            Score = Math.Round(score, 3),
            FitConfidence = confidence,
            Reason = BuildReason(track, query, confidence),
            BestUseCase = bestUseCase,
            SecondaryUseCases = InferSecondaryUseCases(track, bestUseCase),
            WhyThisWorks = BuildWhyThisWorks(track, query, confidence),
            Attributes = BuildAttributes(track),
            License = BuildLicenseSummary(track, feeRate),
            Preview = BuildPreview(track)
        };
    }

    public static TrackDetailsDto BuildDetails(
        Track track,
        decimal feeRate,
        ApplicationUser? creator,
        string? slug,
        string? profileImageUrl)
    {
        return new TrackDetailsDto
        {
            TrackId = track.Id.ToString(),
            CambrianTrackId = track.CambrianTrackId,
            Title = track.Title,
            Description = track.Description,
            Creator = BuildCreatorSummary(track, creator, slug, profileImageUrl),
            Attributes = BuildAttributes(track),
            Preview = BuildPreview(track),
            License = BuildLicenseSummary(track, feeRate),
            Status = track.Status ?? "available",
            Visibility = track.Visibility,
            ExclusiveSold = track.ExclusiveSold,
            IsCopyrightTransferred = track.CopyrightOwnerId != null,
            TrendingScore = track.TrendingScore,
            CreatedAt = track.CreatedAt
        };
    }

    public static List<LicenseOptionDto> BuildLicenseOptions(Track track)
    {
        var options = new List<LicenseOptionDto>();

        var nonExCents = track.NonExclusivePriceCents > 0
            ? track.NonExclusivePriceCents
            : (int)(track.Price * 100);

        options.Add(new LicenseOptionDto
        {
            LicenseType = "nonexclusive",
            PriceCents = nonExCents,
            PriceDollars = nonExCents / 100m,
            Available = true,
            AllowedUses = new List<string>
            {
                "Commercial use in media projects",
                "YouTube / video content",
                "Podcasts and streaming",
                "Advertising and social media"
            },
            Restrictions = new List<string>
            {
                "Credit to original creator required",
                "No resale of license",
                "Track remains available on marketplace"
            }
        });

        if (!track.ExclusiveSold)
        {
            var exCents = track.ExclusivePriceCents > 0
                ? track.ExclusivePriceCents
                : nonExCents;

            options.Add(new LicenseOptionDto
            {
                LicenseType = "exclusive",
                PriceCents = exCents,
                PriceDollars = exCents / 100m,
                Available = true,
                AllowedUses = new List<string>
                {
                    "Exclusive commercial use",
                    "Perpetual license",
                    "Unlimited projects",
                    "Global distribution"
                },
                Restrictions = new List<string>
                {
                    "No resale of standalone track",
                    "No sublicensing"
                }
            });
        }

        if (!track.ExclusiveSold && track.CopyrightOwnerId == null)
        {
            var buyoutCents = track.CopyrightBuyoutPriceCents > 0
                ? track.CopyrightBuyoutPriceCents
                : (track.ExclusivePriceCents > 0 ? track.ExclusivePriceCents : nonExCents);

            options.Add(new LicenseOptionDto
            {
                LicenseType = "copyright_buyout",
                PriceCents = buyoutCents,
                PriceDollars = buyoutCents / 100m,
                Available = true,
                AllowedUses = new List<string>
                {
                    "Full copyright ownership transfer",
                    "Perpetual and irrevocable rights",
                    "Unlimited commercial use",
                    "Sublicensing rights"
                },
                Restrictions = new List<string>
                {
                    "Track permanently removed from marketplace",
                    "Original creator relinquishes all rights"
                }
            });
        }

        return options;
    }

    // ── Private builders ──

    private static CreatorSummaryDto BuildCreatorSummary(
        Track track, ApplicationUser? creator, string? slug, string? profileImageUrl)
    {
        var canonicalSlug = track.CreatorEntity?.Username ?? slug;
        var canonicalImage = track.CreatorEntity?.ProfileImageUrl ?? profileImageUrl;
        var displayName = !string.IsNullOrWhiteSpace(track.CreatorEntity?.DisplayName)
            ? track.CreatorEntity.DisplayName
            : track.CreatorEntity?.Username
              ?? creator?.DisplayName
              ?? "Unknown Artist";

        return new CreatorSummaryDto
        {
            UserId = track.CreatorId,
            Username = track.CreatorEntity?.Username ?? slug,
            DisplayName = displayName,
            ProfileImageUrl = canonicalImage,
            Slug = canonicalSlug
        };
    }

    private static TrackAttributesDto BuildAttributes(Track track) => new()
    {
        Genre = track.Genre,
        Mood = track.Mood,
        Tempo = track.Tempo,
        Instrumental = track.Instrumental,
        Duration = track.Duration,
        Tags = track.Tags?.ToList() ?? new List<string>()
    };

    private static TrackPreviewDto BuildPreview(Track track) => new()
    {
        AudioUrl = track.AudioUrl,
        CoverArtUrl = track.CoverArtUrl,
        Duration = track.Duration
    };

    private static LicenseSummaryDto BuildLicenseSummary(Track track, decimal feeRate)
    {
        var options = BuildLicenseOptions(track);
        var cheapest = options.MinBy(o => o.PriceCents) ?? options.FirstOrDefault();

        return new LicenseSummaryDto
        {
            CheapestLicenseType = cheapest?.LicenseType ?? "nonexclusive",
            CheapestPriceCents = cheapest?.PriceCents ?? 0,
            CheapestPriceDollars = cheapest?.PriceDollars ?? 0,
            ExclusiveAvailable = !track.ExclusiveSold,
            CopyrightBuyoutAvailable = !track.ExclusiveSold && track.CopyrightOwnerId == null,
            Options = options
        };
    }

    private static string BuildReason(Track track, SearchTracksQuery query, string confidence)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(query.Query) && !string.IsNullOrEmpty(track.Title)
            && track.Title.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
            parts.Add("title matches your search");

        if (!string.IsNullOrEmpty(query.Genre) && string.Equals(track.Genre, query.Genre, StringComparison.OrdinalIgnoreCase))
            parts.Add($"genre is {track.Genre}");

        if (!string.IsNullOrEmpty(query.Mood) && string.Equals(track.Mood, query.Mood, StringComparison.OrdinalIgnoreCase))
            parts.Add($"mood is {track.Mood}");

        if (!string.IsNullOrEmpty(query.UseCase) && string.Equals(track.UseCase, query.UseCase, StringComparison.OrdinalIgnoreCase))
            parts.Add($"designed for {track.UseCase} use");

        if (track.TrendingScore > 50)
            parts.Add("currently trending");

        if (parts.Count == 0)
            parts.Add(confidence == "high" ? "strong overall match" : "general catalog match");

        return string.Join("; ", parts);
    }

    private static string BuildWhyThisWorks(Track track, SearchTracksQuery query, string confidence)
    {
        var mood = track.Mood ?? "versatile";
        var genre = track.Genre ?? "mixed-genre";
        var useCase = query.UseCase ?? track.UseCase ?? "content creation";

        return confidence switch
        {
            "high" => $"This {mood} {genre} track is an excellent fit for {useCase}. "
                     + (track.Instrumental ? "Being instrumental, it won't compete with voiceover or dialogue." : ""),
            "medium" => $"A {mood} {genre} track that works well for {useCase}.",
            _ => $"A {genre} track that could complement {useCase} projects."
        };
    }

    private static List<string> InferSecondaryUseCases(Track track, string primaryUseCase)
    {
        var useCases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vlog", "podcast", "gaming", "ads", "film", "social media"
        };
        useCases.Remove(primaryUseCase);

        // Instrumental tracks are more versatile for background use
        if (track.Instrumental)
            return useCases.Take(3).ToList();

        return useCases.Take(2).ToList();
    }
}
