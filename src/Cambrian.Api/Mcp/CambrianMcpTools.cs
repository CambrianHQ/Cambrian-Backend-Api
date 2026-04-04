using System.ComponentModel;
using System.Text.Json;
using Cambrian.Application.AI.Discovery.Dtos;
using Cambrian.Application.AI.Discovery.Queries;
using Cambrian.Application.AI.Discovery.Services;
using ModelContextProtocol.Server;

namespace Cambrian.Api.Mcp;

[McpServerToolType]
public class CambrianMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [McpServerTool(Name = "search_tracks"), Description(
        "Search the Cambrian music catalogue. Accepts a free-text query and/or structured " +
        "filters (genre, mood, useCase, bpm, key, instrumentalOnly, minDurationSeconds, " +
        "maxDurationSeconds). Returns ranked results with relevance scores and licensing info.")]
    public static async Task<string> SearchTracks(
        ITrackDiscoveryService discovery,
        [Description("Free-text search query (e.g. 'chill lofi beats for a cooking vlog')")] string? query = null,
        [Description("Intended use case: vlog, podcast, gaming, ads, etc.")] string? useCase = null,
        [Description("Music genre filter")] string? genre = null,
        [Description("Mood filter (e.g. chill, energetic, dark)")] string? mood = null,
        [Description("Target BPM")] int? bpm = null,
        [Description("Musical key (e.g. C major, A minor)")] string? key = null,
        [Description("Return only instrumental tracks")] bool instrumentalOnly = false,
        [Description("Allow tracks with vocals")] bool vocalsAllowed = true,
        [Description("Only tracks allowing commercial use")] bool commercialUseRequired = false,
        [Description("Minimum duration in seconds")] int? minDurationSeconds = null,
        [Description("Maximum duration in seconds")] int? maxDurationSeconds = null,
        [Description("Page number (default 1)")] int page = 1,
        [Description("Results per page (default 10, max 50)")] int pageSize = 10)
    {
        var searchQuery = new SearchTracksQuery
        {
            Query = query,
            UseCase = useCase,
            Genre = genre,
            Mood = mood,
            Bpm = bpm,
            Key = key,
            InstrumentalOnly = instrumentalOnly,
            VocalsAllowed = vocalsAllowed,
            CommercialUseRequired = commercialUseRequired,
            MinDurationSeconds = minDurationSeconds,
            MaxDurationSeconds = maxDurationSeconds,
            Page = page,
            PageSize = Math.Clamp(pageSize, 1, 50)
        };

        AiTrackSearchResponseDto results = await discovery.SearchAsync(searchQuery);
        return JsonSerializer.Serialize(results, JsonOptions);
    }

    [McpServerTool(Name = "get_track_details"), Description(
        "Get full details for a specific track including attributes, licensing, " +
        "creator info, and preview URLs.")]
    public static async Task<string> GetTrackDetails(
        ITrackDiscoveryService discovery,
        [Description("Track ID (CambrianTrackId like 'CAMB-TRK-A1B2C3D4' or GUID)")] string trackId)
    {
        var details = await discovery.GetTrackDetailsAsync(trackId);

        if (details is null)
            return JsonSerializer.Serialize(new { error = "Track not found" }, JsonOptions);

        return JsonSerializer.Serialize(details, JsonOptions);
    }

    [McpServerTool(Name = "get_track_licenses"), Description(
        "Get all available license options for a track with prices, " +
        "allowed uses, and restrictions.")]
    public static async Task<string> GetTrackLicenses(
        ITrackDiscoveryService discovery,
        [Description("Track ID (CambrianTrackId or GUID)")] string trackId)
    {
        var options = await discovery.GetLicenseOptionsAsync(trackId);

        if (options is null)
            return JsonSerializer.Serialize(new { error = "Track not found" }, JsonOptions);

        return JsonSerializer.Serialize(options, JsonOptions);
    }

    [McpServerTool(Name = "get_track_preview"), Description(
        "Get preview info for a track including audio URL, duration, and format.")]
    public static async Task<string> GetTrackPreview(
        ITrackDiscoveryService discovery,
        [Description("Track ID (CambrianTrackId or GUID)")] string trackId)
    {
        var preview = await discovery.GetPreviewAsync(trackId);

        if (preview is null)
            return JsonSerializer.Serialize(new { error = "Track not found" }, JsonOptions);

        return JsonSerializer.Serialize(preview, JsonOptions);
    }

    [McpServerTool(Name = "get_creator_profile"), Description(
        "Get a creator's public profile including bio, track count, " +
        "featured genres/moods, and catalog highlights.")]
    public static async Task<string> GetCreatorProfile(
        ITrackDiscoveryService discovery,
        [Description("Creator username or user ID")] string creatorId)
    {
        var profile = await discovery.GetCreatorProfileAsync(creatorId);

        if (profile is null)
            return JsonSerializer.Serialize(new { error = "Creator not found" }, JsonOptions);

        return JsonSerializer.Serialize(profile, JsonOptions);
    }
}
