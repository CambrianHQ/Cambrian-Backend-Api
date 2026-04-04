using Cambrian.Application.AI.Discovery.Queries;
using Cambrian.Application.AI.Discovery.Ranking;
using Cambrian.Domain.Entities;

namespace Cambrian.Api.Tests.AI;

public class TrackRankingServiceTests
{
    private readonly TrackRankingService _sut = new();

    // ── Helpers ──

    private static Track MakeTrack(
        string title = "Test Track",
        string? genre = null,
        string? mood = null,
        string? tempo = null,
        bool instrumental = false,
        string? useCase = null,
        string? duration = null,
        string? description = null,
        decimal trendingScore = 0)
    {
        return new Track
        {
            Id = Guid.NewGuid(),
            CambrianTrackId = $"CAMB-TRK-{Guid.NewGuid().ToString()[..8].ToUpper()}",
            Title = title,
            Description = description,
            Genre = genre,
            Mood = mood,
            Tempo = tempo,
            Instrumental = instrumental,
            UseCase = useCase,
            Duration = duration,
            TrendingScore = trendingScore,
            Tags = new List<string>()
        };
    }

    private static SearchTracksQuery MakeQuery(
        string? query = null,
        string? genre = null,
        string? mood = null,
        int? bpm = null,
        bool instrumentalOnly = false,
        string? useCase = null,
        int? minDuration = null,
        int? maxDuration = null)
    {
        return new SearchTracksQuery
        {
            Query = query,
            Genre = genre,
            Mood = mood,
            Bpm = bpm,
            InstrumentalOnly = instrumentalOnly,
            UseCase = useCase,
            MinDurationSeconds = minDuration,
            MaxDurationSeconds = maxDuration
        };
    }

    // ── Genre + Mood matching ──

    [Fact]
    public void ExactGenreAndMood_OutranksLooseMatch()
    {
        var exactTrack = MakeTrack(genre: "lofi", mood: "chill");
        var looseTrack = MakeTrack(genre: "rock", mood: "energetic");
        var query = MakeQuery(genre: "lofi", mood: "chill");

        var exactScore = _sut.ComputeScore(exactTrack, query);
        var looseScore = _sut.ComputeScore(looseTrack, query);

        Assert.True(exactScore > looseScore,
            $"Exact genre+mood score ({exactScore}) should beat loose match ({looseScore})");
    }

    [Fact]
    public void ExactGenre_OnlyPartialMood_ScoresBetween()
    {
        var exactBoth = MakeTrack(genre: "lofi", mood: "chill");
        var genreOnly = MakeTrack(genre: "lofi", mood: "dark");
        var neitherMatch = MakeTrack(genre: "metal", mood: "aggressive");

        var query = MakeQuery(genre: "lofi", mood: "chill");

        var fullScore = _sut.ComputeScore(exactBoth, query);
        var partialScore = _sut.ComputeScore(genreOnly, query);
        var noScore = _sut.ComputeScore(neitherMatch, query);

        Assert.True(fullScore > partialScore, "Full match should outscore partial");
        Assert.True(partialScore > noScore, "Partial match should outscore no match");
    }

    // ── Instrumental filtering ──

    [Fact]
    public void InstrumentalOnly_PenalizesVocalTracks()
    {
        var instrumentalTrack = MakeTrack(instrumental: true);
        var vocalTrack = MakeTrack(instrumental: false);
        var query = MakeQuery(instrumentalOnly: true);

        var instrScore = _sut.ComputeScore(instrumentalTrack, query);
        var vocalScore = _sut.ComputeScore(vocalTrack, query);

        Assert.True(instrScore > vocalScore,
            $"Instrumental ({instrScore}) should beat vocal ({vocalScore}) when instrumentalOnly=true");
    }

    [Fact]
    public void InstrumentalOnly_False_DoesNotPenalize()
    {
        var instrumentalTrack = MakeTrack(genre: "lofi", instrumental: true);
        var vocalTrack = MakeTrack(genre: "lofi", instrumental: false);
        var query = MakeQuery(genre: "lofi", instrumentalOnly: false);

        var instrScore = _sut.ComputeScore(instrumentalTrack, query);
        var vocalScore = _sut.ComputeScore(vocalTrack, query);

        // When instrumentalOnly is false, instrumental attribute is not checked so scores should be equal
        Assert.Equal(instrScore, vocalScore, precision: 3);
    }

    // ── BPM tolerance ──

    [Fact]
    public void Bpm_Within10Tolerance_ScoresHigher()
    {
        var withinRange = MakeTrack(tempo: "125"); // Target 120, within ±10
        var outsideRange = MakeTrack(tempo: "80");  // Target 120, outside ±10
        var query = MakeQuery(bpm: 120);

        var withinScore = _sut.ComputeScore(withinRange, query);
        var outsideScore = _sut.ComputeScore(outsideRange, query);

        Assert.True(withinScore > outsideScore,
            $"BPM within tolerance ({withinScore}) should beat outside ({outsideScore})");
    }

    [Fact]
    public void Bpm_ExactMatch_ScoresHigher()
    {
        var exactBpm = MakeTrack(tempo: "120");
        var outsideBpm = MakeTrack(tempo: "150");
        var query = MakeQuery(bpm: 120);

        var exactScore = _sut.ComputeScore(exactBpm, query);
        var outsideScore = _sut.ComputeScore(outsideBpm, query);

        Assert.True(exactScore > outsideScore);
    }

    [Fact]
    public void Bpm_NamedTempos_MapToNumericValues()
    {
        // "slow" → 80, "medium" → 120, "fast" → 150
        var slowTrack = MakeTrack(tempo: "slow");
        var query = MakeQuery(bpm: 80);

        var score = _sut.ComputeScore(slowTrack, query);
        // Slow maps to 80, exact match with query of 80 → should get full attribute credit
        Assert.True(score > 0.0);
    }

    // ── Duration bounds ──

    [Fact]
    public void Duration_WithinBounds_ScoresHigher()
    {
        var inRange = MakeTrack(duration: "2:30");   // 150 seconds
        var outOfRange = MakeTrack(duration: "10:00"); // 600 seconds
        var query = MakeQuery(minDuration: 60, maxDuration: 180);

        var inScore = _sut.ComputeScore(inRange, query);
        var outScore = _sut.ComputeScore(outOfRange, query);

        Assert.True(inScore > outScore,
            $"In-range duration ({inScore}) should beat out-of-range ({outScore})");
    }

    [Fact]
    public void Duration_NoBoundsSpecified_NoPenalty()
    {
        var shortTrack = MakeTrack(duration: "0:30");
        var longTrack = MakeTrack(duration: "15:00");
        var query = MakeQuery(); // no duration constraints

        var shortScore = _sut.ComputeScore(shortTrack, query);
        var longScore = _sut.ComputeScore(longTrack, query);

        // Without duration constraints, they should score the same
        Assert.Equal(shortScore, longScore, precision: 3);
    }

    // ── Text match ──

    [Fact]
    public void TextMatch_TitleMatch_ScoresHighest()
    {
        var titleMatch = MakeTrack(title: "Chill Vibes", description: "A rock anthem");
        var descriptionMatch = MakeTrack(title: "Rock Anthem", description: "Chill background music");
        var query = MakeQuery(query: "chill");

        var titleScore = _sut.ComputeScore(titleMatch, query);
        var descScore = _sut.ComputeScore(descriptionMatch, query);

        Assert.True(titleScore > descScore,
            $"Title match ({titleScore}) should outscore description-only match ({descScore})");
    }

    [Fact]
    public void TextMatch_NoQuery_ReturnsNeutralScore()
    {
        var track = MakeTrack(title: "Anything");
        var query = MakeQuery(query: null);

        var score = _sut.ComputeScore(track, query);

        // With no query text, text score is 0.5 (neutral), so total should be non-zero
        Assert.True(score > 0.0);
    }

    // ── Use-case matching ──

    [Fact]
    public void UseCase_ExactMatch_OutranksNoMatch()
    {
        var exactUseCase = MakeTrack(useCase: "podcast");
        var differentUseCase = MakeTrack(useCase: "gaming");
        var query = MakeQuery(useCase: "podcast");

        var exactScore = _sut.ComputeScore(exactUseCase, query);
        var diffScore = _sut.ComputeScore(differentUseCase, query);

        Assert.True(exactScore > diffScore,
            $"Exact use-case ({exactScore}) should beat different ({diffScore})");
    }

    [Fact]
    public void UseCase_NullTrackUseCase_ScoresLowerThanExact()
    {
        var withUseCase = MakeTrack(useCase: "vlog");
        var noUseCase = MakeTrack(useCase: null);
        var query = MakeQuery(useCase: "vlog");

        var withScore = _sut.ComputeScore(withUseCase, query);
        var noScore = _sut.ComputeScore(noUseCase, query);

        Assert.True(withScore > noScore);
    }

    // ── Trending normalization ──

    [Fact]
    public void Trending_HighScore_BoostsRanking()
    {
        var trending = MakeTrack(trendingScore: 500);
        var notTrending = MakeTrack(trendingScore: 0);
        var query = MakeQuery();

        var trendingScore = _sut.ComputeScore(trending, query);
        var flatScore = _sut.ComputeScore(notTrending, query);

        Assert.True(trendingScore > flatScore,
            $"Trending track ({trendingScore}) should outscore non-trending ({flatScore})");
    }

    [Fact]
    public void Score_AlwaysBetweenZeroAndOne()
    {
        var track = MakeTrack(
            genre: "lofi", mood: "chill", tempo: "120",
            instrumental: true, useCase: "vlog", duration: "3:00",
            trendingScore: 1000);

        var query = MakeQuery(
            query: "lofi", genre: "lofi", mood: "chill",
            bpm: 120, instrumentalOnly: true, useCase: "vlog",
            minDuration: 60, maxDuration: 300);

        var score = _sut.ComputeScore(track, query);

        Assert.InRange(score, 0.0, 1.0);
    }

    // ── Combined ranking ──

    [Fact]
    public void CombinedScore_PerfectMatch_ScoresAbove07()
    {
        var track = MakeTrack(
            title: "Lofi Chill Vibes",
            genre: "lofi",
            mood: "chill",
            tempo: "120",
            instrumental: true,
            useCase: "vlog",
            duration: "3:00",
            trendingScore: 200);

        var query = MakeQuery(
            query: "lofi",
            genre: "lofi",
            mood: "chill",
            bpm: 120,
            instrumentalOnly: true,
            useCase: "vlog",
            minDuration: 60,
            maxDuration: 300);

        var score = _sut.ComputeScore(track, query);

        Assert.True(score >= 0.7, $"Perfect match should score >= 0.7, got {score}");
    }

    [Fact]
    public void CombinedScore_NoMatchAtAll_ScoresBelow04()
    {
        var track = MakeTrack(
            title: "Death Metal Blast",
            genre: "metal",
            mood: "aggressive",
            tempo: "200",
            instrumental: false,
            useCase: "gaming");

        var query = MakeQuery(
            query: "jazz",
            genre: "jazz",
            mood: "smooth",
            bpm: 90,
            instrumentalOnly: true,
            useCase: "podcast");

        var score = _sut.ComputeScore(track, query);

        Assert.True(score < 0.4, $"Total mismatch should score < 0.4, got {score}");
    }
}
