using System.Reflection;
using System.Text.Json;
using Cambrian.Application.AI.Discovery.Dtos;

namespace Cambrian.Api.Tests.AI;

/// <summary>
/// Verifies the AI Discovery OpenAPI contract stays in sync with the C# DTOs
/// and that all expected paths and schemas exist.
/// </summary>
public sealed class AiDiscoveryContractTests
{
    private static JsonDocument LoadOpenApi()
    {
        var basePath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "contracts", "openapi.v1.json");
        var json = File.ReadAllText(Path.GetFullPath(basePath));
        return JsonDocument.Parse(json);
    }

    // ── Path existence ──

    [Theory]
    [InlineData("/ai-discovery/tracks/search")]
    [InlineData("/ai-discovery/tracks/{trackId}")]
    [InlineData("/ai-discovery/tracks/{trackId}/preview")]
    [InlineData("/ai-discovery/creators/{creatorId}")]
    public void OpenApi_ContainsAiDiscoveryPath(string expectedPath)
    {
        using var doc = LoadOpenApi();
        var paths = doc.RootElement.GetProperty("paths");

        var found = false;
        foreach (var pathProp in paths.EnumerateObject())
        {
            if (pathProp.Name == expectedPath)
            {
                found = true;
                break;
            }
        }

        Assert.True(found, $"OpenAPI spec is missing path: {expectedPath}");
    }

    // ── Schema existence ──

    [Theory]
    [InlineData("AiTrackSearchResponse")]
    [InlineData("AiTrackSearchResult")]
    [InlineData("AiTrackDetails")]
    [InlineData("AiTrackDetailsResponse")]
    [InlineData("AiTrackPreview")]
    [InlineData("AiTrackPreviewResponse")]
    [InlineData("AiTrackAttributes")]
    [InlineData("AiLicenseOption")]
    [InlineData("AiLicenseSummary")]
    [InlineData("AiTrackLicenseOptionsResponse")]
    [InlineData("AiQuerySummary")]
    [InlineData("AiCreatorSummary")]
    [InlineData("AiCreatorProfile")]
    [InlineData("AiCreatorProfileResponse")]
    [InlineData("AiCreatorCatalogHighlight")]
    public void OpenApi_ContainsAiSchema(string schemaName)
    {
        using var doc = LoadOpenApi();
        var schemas = doc.RootElement
            .GetProperty("components")
            .GetProperty("schemas");

        Assert.True(schemas.TryGetProperty(schemaName, out _),
            $"OpenAPI spec is missing schema: {schemaName}");
    }

    // ── DTO ↔ Schema property parity ──

    [Fact]
    public void AiLicenseSummary_PropertiesMatchSpec()
    {
        AssertDtoMatchesSchema<AiLicenseSummary>("AiLicenseSummary");
    }

    [Fact]
    public void AiLicenseOption_PropertiesMatchSpec()
    {
        AssertDtoMatchesSchema<AiLicenseOption>("AiLicenseOption");
    }

    [Fact]
    public void AiTrackSearchResult_PropertiesMatchSpec()
    {
        AssertDtoMatchesSchema<AiTrackSearchResult>("AiTrackSearchResult");
    }

    [Fact]
    public void AiTrackDetails_PropertiesMatchSpec()
    {
        AssertDtoMatchesSchema<AiTrackDetails>("AiTrackDetails");
    }

    [Fact]
    public void AiTrackPreview_PropertiesMatchSpec()
    {
        AssertDtoMatchesSchema<AiTrackPreview>("AiTrackPreview");
    }

    [Fact]
    public void AiTrackAttributes_PropertiesMatchSpec()
    {
        AssertDtoMatchesSchema<AiTrackAttributes>("AiTrackAttributes");
    }

    [Fact]
    public void AiCreatorSummary_PropertiesMatchSpec()
    {
        AssertDtoMatchesSchema<AiCreatorSummary>("AiCreatorSummary");
    }

    [Fact]
    public void AiCreatorProfile_PropertiesMatchSpec()
    {
        AssertDtoMatchesSchema<AiCreatorProfile>("AiCreatorProfile");
    }

    [Fact]
    public void AiCreatorCatalogHighlight_PropertiesMatchSpec()
    {
        AssertDtoMatchesSchema<AiCreatorCatalogHighlight>("AiCreatorCatalogHighlight");
    }

    [Fact]
    public void AiQuerySummary_PropertiesMatchSpec()
    {
        AssertDtoMatchesSchema<AiQuerySummary>("AiQuerySummary");
    }

    [Fact]
    public void AiTrackSearchResponse_PropertiesMatchSpec()
    {
        AssertDtoMatchesSchema<AiTrackSearchResponse>("AiTrackSearchResponse");
    }

    // ── Response wrapper shapes ──

    [Fact]
    public void TrackDetailsResponse_WrapsInTrackProperty()
    {
        using var doc = LoadOpenApi();
        var schema = GetSchema(doc, "AiTrackDetailsResponse");
        var props = GetPropertyNames(schema);

        Assert.Contains("track", props);
    }

    [Fact]
    public void LicenseOptionsResponse_WrapsInLicensesProperty()
    {
        using var doc = LoadOpenApi();
        var schema = GetSchema(doc, "AiTrackLicenseOptionsResponse");
        var props = GetPropertyNames(schema);

        Assert.Contains("licenses", props);
    }

    [Fact]
    public void PreviewResponse_WrapsInPreviewProperty()
    {
        using var doc = LoadOpenApi();
        var schema = GetSchema(doc, "AiTrackPreviewResponse");
        var props = GetPropertyNames(schema);

        Assert.Contains("preview", props);
    }

    [Fact]
    public void CreatorProfileResponse_WrapsInCreatorProperty()
    {
        using var doc = LoadOpenApi();
        var schema = GetSchema(doc, "AiCreatorProfileResponse");
        var props = GetPropertyNames(schema);

        Assert.Contains("creator", props);
    }

    // ── All search endpoints are GET ──

    [Fact]
    public void Search_IsGetMethod()
    {
        using var doc = LoadOpenApi();
        var path = doc.RootElement.GetProperty("paths").GetProperty("/ai-discovery/tracks/search");

        Assert.True(path.TryGetProperty("get", out _), "Search endpoint must be GET");
    }

    // ── Helpers ──

    private static void AssertDtoMatchesSchema<T>(string schemaName)
    {
        using var doc = LoadOpenApi();
        var schema = GetSchema(doc, schemaName);
        var specProps = GetPropertyNames(schema);

        var dtoProps = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToHashSet();

        // Every spec property must exist on the DTO
        var missingFromDto = specProps.Except(dtoProps).ToList();
        Assert.True(missingFromDto.Count == 0,
            $"{schemaName}: OpenAPI spec has properties not found on DTO: {string.Join(", ", missingFromDto)}");

        // Every DTO property must exist in the spec
        var missingFromSpec = dtoProps.Except(specProps).ToList();
        Assert.True(missingFromSpec.Count == 0,
            $"{schemaName}: DTO has properties not found in OpenAPI spec: {string.Join(", ", missingFromSpec)}");
    }

    private static JsonElement GetSchema(JsonDocument doc, string name)
    {
        return doc.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(name);
    }

    private static HashSet<string> GetPropertyNames(JsonElement schema)
    {
        var props = new HashSet<string>();
        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var prop in properties.EnumerateObject())
            {
                props.Add(prop.Name);
            }
        }
        return props;
    }
}
