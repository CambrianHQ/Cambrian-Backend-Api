using System.Text.Json;

namespace Cambrian.Api.Tests.Contract;

public sealed class TrackAiDisclosureContractTests
{
    private static JsonDocument LoadOpenApi()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "contracts", "openapi.v1.json"));
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void OpenApi_ContainsDisclosureLifecyclePaths()
    {
        using var doc = LoadOpenApi();
        var paths = doc.RootElement.GetProperty("paths");
        var disclosure = paths.GetProperty("/api/v1/tracks/{trackId}/ai-disclosure");
        Assert.True(disclosure.TryGetProperty("get", out _));
        Assert.True(disclosure.TryGetProperty("post", out _));
        Assert.True(disclosure.TryGetProperty("put", out _));
        Assert.True(paths.GetProperty("/api/v1/tracks/{trackId}/ai-disclosure/revoke").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/api/v1/tracks/{trackId}/ai-disclosure/history").TryGetProperty("get", out _));
    }

    [Fact]
    public void ClassificationSchema_IsNamedStringEnum()
    {
        using var doc = LoadOpenApi();
        var schema = Schemas(doc).GetProperty("PublicTrackAiDisclosureDto")
            .GetProperty("properties").GetProperty("classification");
        Assert.Equal("string", schema.GetProperty("type").GetString());
        var values = schema.GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "Unclassified", "AIGenerated", "AIAssisted" }, values);

        var requestSchema = Schemas(doc).GetProperty("UpsertTrackAiDisclosureRequest")
            .GetProperty("properties").GetProperty("classification");
        Assert.Equal(values, requestSchema.GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public void StructuredDetailsSchema_ContainsEveryGranularField()
    {
        using var doc = LoadOpenApi();
        var properties = Schemas(doc).GetProperty("AiDisclosureDetailsDto").GetProperty("properties");
        var expected = new[]
        {
            "aiVocals", "aiPrimaryInstruments", "aiComposition", "aiLyrics", "aiPostProduction",
            "aiArtwork", "aiVideo", "generatorTool", "modelVersion", "creationDate",
            "commercialUseLicenseBasis", "voiceLikenessAuthorization", "humanWrittenLyrics",
            "humanVocals", "humanInstruments", "arrangementEditing", "dawWork", "collaborators",
            "humanContributionNarrative",
        };
        foreach (var name in expected) Assert.True(properties.TryGetProperty(name, out _), $"Missing public disclosure field: {name}");
    }

    private static JsonElement Schemas(JsonDocument doc) => doc.RootElement.GetProperty("components").GetProperty("schemas");
}
