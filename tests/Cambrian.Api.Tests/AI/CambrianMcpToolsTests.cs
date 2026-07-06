using System.Text.Json;
using Cambrian.Api.Mcp;
using Cambrian.Application.AI.Discovery.Dtos;
using Cambrian.Application.AI.Discovery.Services;
using NSubstitute;

namespace Cambrian.Api.Tests.AI;

public class CambrianMcpToolsTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ITrackDiscoveryService _discovery = Substitute.For<ITrackDiscoveryService>();

    // ── search_tracks ──

    [Fact]
    public async Task SearchTracks_ReturnsValidJson()
    {
        _discovery.SearchAsync(Arg.Any<Application.AI.Discovery.Queries.SearchTracksQuery>())
            .Returns(new AiTrackSearchResponse
            {
                Results = new List<AiTrackSearchResult>
                {
                    new()
                    {
                        TrackId = "CAMB-TRK-TEST0001",
                        Title = "Test Track",
                        Score = 0.85,
                        FitConfidence = "high"
                    }
                },
                Page = 1,
                PageSize = 10,
                TotalCount = 1,
                QuerySummary = new AiQuerySummary { Intent = "test search" }
            });

        var json = await CambrianMcpTools.SearchTracks(
            _discovery, query: "test", useCase: "vlog");

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
        Assert.Equal("CAMB-TRK-TEST0001", root.GetProperty("results")[0].GetProperty("trackId").GetString());
        Assert.Equal("high", root.GetProperty("results")[0].GetProperty("fitConfidence").GetString());
    }

    [Fact]
    public async Task SearchTracks_MapsAllParametersToQuery()
    {
        Application.AI.Discovery.Queries.SearchTracksQuery? captured = null;

        _discovery.SearchAsync(Arg.Do<Application.AI.Discovery.Queries.SearchTracksQuery>(q => captured = q))
            .Returns(new AiTrackSearchResponse());

        await CambrianMcpTools.SearchTracks(
            _discovery,
            query: "lofi beats",
            useCase: "podcast",
            genre: "lofi",
            mood: "chill",
            bpm: 90,
            key: "C major",
            instrumentalOnly: true,
            vocalsAllowed: false,
            commercialUseRequired: true,
            minDurationSeconds: 60,
            maxDurationSeconds: 300,
            page: 2,
            pageSize: 25);

        Assert.NotNull(captured);
        Assert.Equal("lofi beats", captured!.Query);
        Assert.Equal("podcast", captured.UseCase);
        Assert.Equal("lofi", captured.Genre);
        Assert.Equal("chill", captured.Mood);
        Assert.Equal(90, captured.Bpm);
        Assert.Equal("C major", captured.Key);
        Assert.True(captured.InstrumentalOnly);
        Assert.False(captured.VocalsAllowed);
        Assert.True(captured.CommercialUseRequired);
        Assert.Equal(60, captured.MinDurationSeconds);
        Assert.Equal(300, captured.MaxDurationSeconds);
        Assert.Equal(2, captured.Page);
        Assert.Equal(25, captured.PageSize);
    }

    [Fact]
    public async Task SearchTracks_ClampsPageSizeTo50()
    {
        Application.AI.Discovery.Queries.SearchTracksQuery? captured = null;

        _discovery.SearchAsync(Arg.Do<Application.AI.Discovery.Queries.SearchTracksQuery>(q => captured = q))
            .Returns(new AiTrackSearchResponse());

        await CambrianMcpTools.SearchTracks(_discovery, pageSize: 999);

        Assert.NotNull(captured);
        Assert.Equal(50, captured!.PageSize);
    }

    [Fact]
    public async Task SearchTracks_DefaultParams_ProduceValidQuery()
    {
        Application.AI.Discovery.Queries.SearchTracksQuery? captured = null;

        _discovery.SearchAsync(Arg.Do<Application.AI.Discovery.Queries.SearchTracksQuery>(q => captured = q))
            .Returns(new AiTrackSearchResponse());

        await CambrianMcpTools.SearchTracks(_discovery);

        Assert.NotNull(captured);
        Assert.Null(captured!.Query);
        Assert.Null(captured.Genre);
        Assert.False(captured.InstrumentalOnly);
        Assert.Equal(1, captured.Page);
        Assert.Equal(10, captured.PageSize);
    }

    // ── get_track_details ──

    [Fact]
    public async Task GetTrackDetails_ReturnsTrackJson()
    {
        _discovery.GetTrackDetailsAsync("CAMB-TRK-TEST0001")
            .Returns(new AiTrackDetails
            {
                TrackId = "CAMB-TRK-TEST0001",
                Title = "Detail Track",
                WhyThisWorks = "Fits perfectly."
            });

        var json = await CambrianMcpTools.GetTrackDetails(_discovery, "CAMB-TRK-TEST0001");
        var doc = JsonDocument.Parse(json);

        Assert.Equal("CAMB-TRK-TEST0001", doc.RootElement.GetProperty("trackId").GetString());
        Assert.Equal("Detail Track", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetTrackDetails_NotFound_ReturnsErrorJson()
    {
        _discovery.GetTrackDetailsAsync("CAMB-TRK-MISSING")
            .Returns((AiTrackDetails?)null);

        var json = await CambrianMcpTools.GetTrackDetails(_discovery, "CAMB-TRK-MISSING");
        var doc = JsonDocument.Parse(json);

        Assert.Equal("Track not found", doc.RootElement.GetProperty("error").GetString());
    }

    // ── get_track_preview ──

    [Fact]
    public async Task GetTrackPreview_ReturnsPreviewJson()
    {
        _discovery.GetPreviewAsync("CAMB-TRK-TEST0001")
            .Returns(new AiTrackPreview
            {
                Available = true,
                Url = "https://cdn.example.com/audio/test.mp3",
                DurationSeconds = 180,
                Format = "mp3"
            });

        var json = await CambrianMcpTools.GetTrackPreview(_discovery, "CAMB-TRK-TEST0001");
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("mp3", doc.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task GetTrackPreview_NullUrl_ProducesSafeOutput()
    {
        _discovery.GetPreviewAsync("CAMB-TRK-NOAUDIO")
            .Returns(new AiTrackPreview
            {
                Available = false,
                Url = null,
                DurationSeconds = null,
                Format = null
            });

        var json = await CambrianMcpTools.GetTrackPreview(_discovery, "CAMB-TRK-NOAUDIO");
        var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("url").ValueKind);
    }

    [Fact]
    public async Task GetTrackPreview_NotFound_ReturnsErrorJson()
    {
        _discovery.GetPreviewAsync("CAMB-TRK-GONE")
            .Returns((AiTrackPreview?)null);

        var json = await CambrianMcpTools.GetTrackPreview(_discovery, "CAMB-TRK-GONE");
        var doc = JsonDocument.Parse(json);

        Assert.Equal("Track not found", doc.RootElement.GetProperty("error").GetString());
    }

    // ── get_creator_profile ──

    [Fact]
    public async Task GetCreatorProfile_ReturnsProfileJson()
    {
        _discovery.GetCreatorProfileAsync("creator123")
            .Returns(new AiCreatorProfile
            {
                CreatorId = "creator123",
                DisplayName = "DJ Test",
                VerifiedCreator = true,
                TrackCount = 42
            });

        var json = await CambrianMcpTools.GetCreatorProfile(_discovery, "creator123");
        var doc = JsonDocument.Parse(json);

        Assert.Equal("creator123", doc.RootElement.GetProperty("creatorId").GetString());
        Assert.Equal("DJ Test", doc.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("trackCount").GetInt32());
    }

    [Fact]
    public async Task GetCreatorProfile_NotFound_ReturnsErrorJson()
    {
        _discovery.GetCreatorProfileAsync("nobody")
            .Returns((AiCreatorProfile?)null);

        var json = await CambrianMcpTools.GetCreatorProfile(_discovery, "nobody");
        var doc = JsonDocument.Parse(json);

        Assert.Equal("Creator not found", doc.RootElement.GetProperty("error").GetString());
    }

    // ── JSON output stability ──

    [Fact]
    public async Task AllTools_ProduceCamelCaseJson()
    {
        _discovery.SearchAsync(Arg.Any<Application.AI.Discovery.Queries.SearchTracksQuery>())
            .Returns(new AiTrackSearchResponse
            {
                Results = new List<AiTrackSearchResult>
                {
                    new() { TrackId = "T1", Title = "X", FitConfidence = "low" }
                },
                QuerySummary = new AiQuerySummary { Intent = "test" }
            });

        var json = await CambrianMcpTools.SearchTracks(_discovery, query: "x");

        // Verify camelCase (not PascalCase)
        Assert.Contains("\"totalCount\"", json);
        Assert.Contains("\"querySummary\"", json);
        Assert.Contains("\"fitConfidence\"", json);
        Assert.DoesNotContain("\"TotalCount\"", json);
        Assert.DoesNotContain("\"QuerySummary\"", json);
    }

    [Fact]
    public async Task AllTools_ProduceIndentedJson()
    {
        _discovery.GetTrackDetailsAsync("T1")
            .Returns(new AiTrackDetails { TrackId = "T1", Title = "X" });

        var json = await CambrianMcpTools.GetTrackDetails(_discovery, "T1");

        // Indented JSON will have newlines
        Assert.Contains("\n", json);
    }
}
