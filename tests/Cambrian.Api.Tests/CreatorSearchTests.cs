using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// B8 regression: POST /creators/search must return matching creators. The endpoint previously
/// did not exist (404), so the Search page's Creators tab always showed "No Results".
/// </summary>
public sealed class CreatorSearchTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public CreatorSearchTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Search_ByUsername_ReturnsMatch_ExcludesNonMatch()
    {
        var marker = $"qa{Guid.NewGuid():N}"[..10];
        await _fixture.SeedCreatorAsync($"user-{Guid.NewGuid():N}", marker + "match");
        await _fixture.SeedCreatorAsync($"user-{Guid.NewGuid():N}", $"other{Guid.NewGuid():N}"[..14]);

        var client = _fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/creators/search", new { query = marker });
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.True(data.GetArrayLength() >= 1);
        foreach (var c in data.EnumerateArray())
            Assert.Contains(marker, c.GetProperty("username").GetString());
    }

    [Fact]
    public async Task Search_BlankQuery_ReturnsEmptyArray()
    {
        var client = _fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/creators/search", new { query = "" });
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal(0, data.GetArrayLength());
    }
}
