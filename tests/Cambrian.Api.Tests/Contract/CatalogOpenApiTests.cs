using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests.Contract;

public sealed class CatalogOpenApiTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public CatalogOpenApiTests(CambrianApiFixture factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Catalog_OpenApi_Describes_Query_Params_And_Paginated_Response()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var catalogGet = doc.RootElement
            .GetProperty("paths")
            .GetProperty("/catalog")
            .GetProperty("get");

        var paramNames = catalogGet
            .GetProperty("parameters")
            .EnumerateArray()
            .Select(p => p.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("page", paramNames);
        Assert.Contains("pageSize", paramNames);
        Assert.Contains("genre", paramNames);
        Assert.Contains("search", paramNames);
        Assert.Contains("sort", paramNames);
        Assert.Contains("mood", paramNames);
        Assert.Contains("tempo", paramNames);
        Assert.Contains("instrumental", paramNames);
        Assert.Contains("duration", paramNames);

        var schema = catalogGet
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");

        var schemaRef = schema.GetProperty("$ref").GetString();
        Assert.False(string.IsNullOrWhiteSpace(schemaRef));

        var schemaName = schemaRef!.Split('/').Last();
        var responseSchema = doc.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(schemaName);

        var properties = responseSchema.GetProperty("properties");
        Assert.Equal("boolean", properties.GetProperty("success").GetProperty("type").GetString());
        Assert.Equal("array", properties.GetProperty("data").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("page").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("pageSize").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("totalCount").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("totalPages").GetProperty("type").GetString());
        Assert.Equal("boolean", properties.GetProperty("hasNextPage").GetProperty("type").GetString());
        Assert.Equal("boolean", properties.GetProperty("hasPreviousPage").GetProperty("type").GetString());
    }
}
