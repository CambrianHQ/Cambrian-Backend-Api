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
    public static AiTrackSearchResult Build(
        Track track,
        double score,
        SearchTracksQuery query,
        ApplicationUser? creator)
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

        return new AiTrackSearchResult
        {
            TrackId = track.CambrianTrackId,
            Title = track.Title,
            Creator = BuildCreatorSummary(track, creator),
            Score = Math.Round(score, 3),
            FitConfidence = confidence,
            Reason = BuildReason(track, query, confidence),
            BestUseCase = bestUseCase,
            SecondaryUseCases = InferSecondaryUseCases(track, bestUseCase),
            WhyThisWorks = BuildWhyThisWorks(track, query, confidence),
            Attributes = BuildAttributes(track),
            License = BuildLicenseSummary(track),
            Preview = BuildPreview(track)
        };
    }

    public static AiTrackDetails BuildDetails(
        Track track,
        ApplicationUser? creator)
    {
        return new AiTrackDetails
        {
            TrackId = track.CambrianTrackId,
            Title = track.Title,
            Description = track.Description,
            Creator = BuildCreatorSummary(track, creator),
            Attributes = BuildAttributes(track),
            Tags = track.Tags?.ToList() ?? new List<string>(),
            UseCaseHints = InferUseCaseHints(track),
            WhyThisWorks = BuildDetailsWhyThisWorks(track),
            LicenseSummary = BuildLicenseSummary(track),
            Preview = BuildPreview(track),
            CoverImageUrl = track.CoverArtUrl
        };
    }

    public static List<AiLicenseOption> BuildLicenseOptions(Track track)
    {
        var options = new List<AiLicenseOption>();

        var nonExCents = track.NonExclusivePriceCents > 0
            ? track.NonExclusivePriceCents
            : (int)(track.Price * 100);

        options.Add(new AiLicenseOption
        {
            DisplayName = "Standard Usage",
            Price = nonExCents / 100m,
            Currency = "USD",
            CommercialUse = true,
            AttributionRequired = true,
            InstantDownload = true,
            Summary = "Use this track in unlimited projects across video, podcasts, and social media.",
            AllowedUseCases = new List<string>
            {
                "YouTube / video content",
                "Podcasts and streaming",
                "Advertising and social media",
                "Commercial media projects"
            },
            Restrictions = new List<string>
            {
                "Credit to the original creator required",
                "No reselling the track as your own"
            },
            RecommendedFor = new List<string>
            {
                "Content creators",
                "Podcasters",
                "Social media marketers"
            }
        });

        return options;
    }

    public static AiQuerySummary BuildQuerySummary(SearchTracksQuery query, int resultCount)
    {
        var matchedOn = new List<string>();

        if (!string.IsNullOrEmpty(query.Genre)) matchedOn.Add($"genre:{query.Genre}");
        if (!string.IsNullOrEmpty(query.Mood)) matchedOn.Add($"mood:{query.Mood}");
        if (query.InstrumentalOnly) matchedOn.Add("instrumental");
        if (!string.IsNullOrEmpty(query.UseCase)) matchedOn.Add($"useCase:{query.UseCase}");
        if (query.Bpm.HasValue) matchedOn.Add($"bpm:{query.Bpm}");
        if (!string.IsNullOrEmpty(query.Key)) matchedOn.Add($"key:{query.Key}");
        if (query.MinDurationSeconds.HasValue) matchedOn.Add($"minDuration:{query.MinDurationSeconds}s");
        if (query.MaxDurationSeconds.HasValue) matchedOn.Add($"maxDuration:{query.MaxDurationSeconds}s");
        if (query.CommercialUseRequired) matchedOn.Add("commercialUse");

        var intent = BuildInterpretedIntent(query);
        var notes = resultCount == 0 ? "No tracks matched current filters. Try broadening your search." : null;

        return new AiQuerySummary
        {
            Intent = intent,
            MatchedOn = matchedOn,
            Notes = notes
        };
    }

    // ── Private builders ──

    private static AiCreatorSummary BuildCreatorSummary(
        Track track, ApplicationUser? creator)
    {
        var displayName = !string.IsNullOrWhiteSpace(track.CreatorEntity?.DisplayName)
            ? track.CreatorEntity.DisplayName
            : track.CreatorEntity?.Username
              ?? creator?.DisplayName
              ?? "Unknown Artist";

        return new AiCreatorSummary
        {
            CreatorId = track.CreatorId,
            DisplayName = displayName,
            VerifiedCreator = creator?.VerifiedCreator
        };
    }

    private static AiTrackAttributes BuildAttributes(Track track)
    {
        var moods = new List<string>();
        if (!string.IsNullOrEmpty(track.Mood)) moods.Add(track.Mood);

        return new AiTrackAttributes
        {
            Genre = track.Genre,
            Moods = moods,
            Bpm = EstimateBpm(track.Tempo),
            DurationSeconds = ParseDurationSeconds(track.Duration),
            Instrumental = track.Instrumental,
            HasVocals = !track.Instrumental,
            EnergyLevel = MapTempoToEnergy(track.Tempo)
        };
    }

    public static AiTrackPreview BuildPreview(Track track) => new()
    {
        Available = !string.IsNullOrEmpty(track.AudioUrl),
        // Never expose the raw object-storage key (e.g. "tracks/{creator}/{guid}.mp3") — it
        // leaks bucket layout/creator ids and isn't directly playable. Emit the public stream
        // proxy route instead, matching the REST controllers' URL rewriting.
        Url = string.IsNullOrEmpty(track.AudioUrl) ? null : $"/stream/{track.Id}/audio",
        DurationSeconds = ParseDurationSeconds(track.Duration),
        Format = InferFormat(track.AudioUrl)
    };

    private static AiLicenseSummary BuildLicenseSummary(Track track)
    {
        var options = BuildLicenseOptions(track);
        var cheapest = options.MinBy(o => o.Price) ?? options.FirstOrDefault();

        var notes = new List<string>();
        if (cheapest?.CommercialUse == true)
            notes.Add("Commercial use is permitted.");

        // Clarity: 1.0 when prices are set and options are clear
        var clarity = options.Count > 0 && cheapest?.Price > 0 ? 1.0 : 0.5;

        return new AiLicenseSummary
        {
            StartingPrice = cheapest?.Price ?? 0,
            Currency = "USD",
            CommercialUse = cheapest?.CommercialUse ?? false,
            AttributionRequired = cheapest?.AttributionRequired ?? true,
            InstantDownload = cheapest?.InstantDownload ?? false,
            LicenseClarityScore = clarity,
            CommercialSafetyNotes = notes
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

    private static string BuildDetailsWhyThisWorks(Track track)
    {
        var mood = track.Mood ?? "versatile";
        var genre = track.Genre ?? "mixed-genre";
        var useCase = track.UseCase ?? "content creation";

        var blurb = $"A {mood} {genre} track suited for {useCase}.";
        if (track.Instrumental)
            blurb += " Being instrumental, it works perfectly as background audio without competing with dialogue.";
        return blurb;
    }

    private static List<string> InferSecondaryUseCases(Track track, string primaryUseCase)
    {
        var useCases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vlog", "podcast", "gaming", "ads", "film", "social media"
        };
        useCases.Remove(primaryUseCase);

        if (track.Instrumental)
            return useCases.Take(3).ToList();

        return useCases.Take(2).ToList();
    }

    private static List<string> InferUseCaseHints(Track track)
    {
        var hints = new List<string>();
        if (!string.IsNullOrEmpty(track.UseCase)) hints.Add(track.UseCase);
        if (track.Instrumental) hints.Add("background music");
        if (!string.IsNullOrEmpty(track.Mood))
        {
            var mood = track.Mood.ToLowerInvariant();
            if (mood.Contains("chill") || mood.Contains("calm")) hints.Add("relaxation");
            if (mood.Contains("energetic") || mood.Contains("upbeat")) hints.Add("workout");
        }
        return hints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string BuildInterpretedIntent(SearchTracksQuery query)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(query.Query)) parts.Add($"searching for \"{query.Query}\"");
        if (!string.IsNullOrEmpty(query.UseCase)) parts.Add($"for {query.UseCase} use");
        if (!string.IsNullOrEmpty(query.Genre)) parts.Add($"in {query.Genre} genre");
        if (!string.IsNullOrEmpty(query.Mood)) parts.Add($"with {query.Mood} mood");
        if (query.InstrumentalOnly) parts.Add("instrumental only");

        return parts.Count > 0 ? string.Join(", ", parts) : "browsing catalogue";
    }

    public static int ParseDurationSeconds(string? duration)
    {
        if (string.IsNullOrEmpty(duration)) return 0;

        // Try "m:ss" or "mm:ss" format
        var parts = duration.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var mins)
            && int.TryParse(parts[1], out var secs))
        {
            return (mins * 60) + secs;
        }

        // Try pure seconds
        if (int.TryParse(duration, out var totalSecs)) return totalSecs;

        return 0;
    }

    public static int EstimateBpm(string? tempo)
    {
        if (string.IsNullOrEmpty(tempo)) return 0;

        // If the tempo is already numeric
        if (int.TryParse(tempo, out var bpm)) return bpm;

        return tempo.ToLowerInvariant() switch
        {
            "slow" => 80,
            "medium" => 120,
            "fast" => 150,
            _ => 0
        };
    }

    private static string? MapTempoToEnergy(string? tempo)
    {
        if (string.IsNullOrEmpty(tempo)) return null;

        return tempo.ToLowerInvariant() switch
        {
            "slow" => "low",
            "medium" => "medium",
            "fast" => "high",
            _ => null
        };
    }

    private static string? InferFormat(string? audioUrl)
    {
        if (string.IsNullOrEmpty(audioUrl)) return null;

        if (audioUrl.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) return "mp3";
        if (audioUrl.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return "wav";
        if (audioUrl.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) return "flac";
        if (audioUrl.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) return "ogg";

        return null;
    }
}
