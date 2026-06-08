using System.ComponentModel;
using System.Text.Json;
using Cambrian.Application.AI.Discovery.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cambrian.Api.Mcp;

[McpServerResourceType]
public class CambrianMcpResources
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [McpServerResource(UriTemplate = "cambrian://tracks/{trackId}", Name = "Track Details", MimeType = "application/json")]
    [Description("Returns full details for a track by its ID (CambrianTrackId or GUID).")]
    public static async Task<ResourceContents> GetTrack(
        ITrackDiscoveryService discovery,
        string trackId)
    {
        var details = await discovery.GetTrackDetailsAsync(trackId);

        return new TextResourceContents
        {
            Uri = $"cambrian://tracks/{trackId}",
            MimeType = "application/json",
            Text = details is not null
                ? JsonSerializer.Serialize(details, JsonOptions)
                : JsonSerializer.Serialize(new { error = "Track not found" }, JsonOptions)
        };
    }

    [McpServerResource(UriTemplate = "cambrian://creators/{creatorId}", Name = "Creator Profile", MimeType = "application/json")]
    [Description("Returns a creator's public profile by username or user ID.")]
    public static async Task<ResourceContents> GetCreator(
        ITrackDiscoveryService discovery,
        string creatorId)
    {
        var profile = await discovery.GetCreatorProfileAsync(creatorId);

        return new TextResourceContents
        {
            Uri = $"cambrian://creators/{creatorId}",
            MimeType = "application/json",
            Text = profile is not null
                ? JsonSerializer.Serialize(profile, JsonOptions)
                : JsonSerializer.Serialize(new { error = "Creator not found" }, JsonOptions)
        };
    }
}
