using Cambrian.Application.AI.Discovery.Queries;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.AI.Discovery.Ranking;

/// <summary>
/// Heuristic scoring engine. Combines text relevance, attribute match depth,
/// trending momentum, and use-case alignment into a single 0–1 score.
/// Pure computation — no I/O.
/// </summary>
public class TrackRankingService : ITrackRankingService
{
    // Weight distribution (must sum to 1.0)
    private const double W_TextMatch = 0.30;
    private const double W_AttributeMatch = 0.25;
    private const double W_UseCaseMatch = 0.25;
    private const double W_Trending = 0.20;

    public double ComputeScore(Track track, SearchTracksQuery query)
    {
        var textScore = ScoreTextMatch(track, query.Query);
        var attrScore = ScoreAttributeMatch(track, query);
        var useCaseScore = ScoreUseCaseMatch(track, query.UseCase);
        var trendingScore = NormalizeTrending(track.TrendingScore);

        var raw = (textScore * W_TextMatch)
                + (attrScore * W_AttributeMatch)
                + (useCaseScore * W_UseCaseMatch)
                + (trendingScore * W_Trending);

        return Math.Clamp(raw, 0.0, 1.0);
    }

    private static double ScoreTextMatch(Track track, string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return 0.5; // neutral when no query

        var q = queryText.ToLowerInvariant();
        double score = 0;

        // Title match (strongest signal)
        if (!string.IsNullOrEmpty(track.Title) && track.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            score += 0.5;

        // Description match
        if (!string.IsNullOrEmpty(track.Description) && track.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            score += 0.2;

        // Tag match
        if (track.Tags?.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)) == true)
            score += 0.2;

        // Genre match
        if (!string.IsNullOrEmpty(track.Genre) && track.Genre.Contains(q, StringComparison.OrdinalIgnoreCase))
            score += 0.1;

        return Math.Min(score, 1.0);
    }

    private static double ScoreAttributeMatch(Track track, SearchTracksQuery query)
    {
        int matched = 0;
        int total = 0;

        if (!string.IsNullOrEmpty(query.Genre))
        {
            total++;
            if (string.Equals(track.Genre, query.Genre, StringComparison.OrdinalIgnoreCase))
                matched++;
        }

        if (!string.IsNullOrEmpty(query.Mood))
        {
            total++;
            if (string.Equals(track.Mood, query.Mood, StringComparison.OrdinalIgnoreCase))
                matched++;
        }

        if (!string.IsNullOrEmpty(query.Tempo))
        {
            total++;
            if (string.Equals(track.Tempo, query.Tempo, StringComparison.OrdinalIgnoreCase))
                matched++;
        }

        if (query.Instrumental.HasValue)
        {
            total++;
            if (track.Instrumental == query.Instrumental.Value)
                matched++;
        }

        if (!string.IsNullOrEmpty(query.Duration))
        {
            total++;
            if (string.Equals(track.Duration, query.Duration, StringComparison.OrdinalIgnoreCase))
                matched++;
        }

        return total == 0 ? 0.5 : (double)matched / total;
    }

    private static double ScoreUseCaseMatch(Track track, string? useCase)
    {
        if (string.IsNullOrWhiteSpace(useCase)) return 0.5;
        if (string.IsNullOrWhiteSpace(track.UseCase)) return 0.3;

        return string.Equals(track.UseCase, useCase, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.2;
    }

    private static double NormalizeTrending(decimal trendingScore)
    {
        // Sigmoid-like normalization: maps 0→0, 100→~0.9, 1000→~0.99
        var d = (double)trendingScore;
        return d / (d + 100.0);
    }
}
