using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Weekly charts ("The Scene") — public reads + admin-triggered aggregation (R17).
/// </summary>
public class ChartsControllerTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public ChartsControllerTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Weekly_is_public_and_returns_chart_shape()
    {
        var client = _fixture.CreateClient();

        var res = await client.GetAsync("/api/charts/weekly");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("weekOf").GetString()));
        Assert.Equal(JsonValueKind.Array, data.GetProperty("entries").ValueKind);
    }

    [Fact]
    public async Task Weekly_alias_path_also_serves()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/charts/weekly");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Aggregate_is_forbidden_for_non_admin()
    {
        var user = await _fixture.CreateAuthenticatedClientAsync("charts-user@test.com", "Test1234!@");

        var res = await user.PostAsync("/admin/charts/aggregate", null);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_can_trigger_aggregation()
    {
        var admin = await _fixture.CreateRoleClientAsync(
            "charts-admin@test.com", "Test1234!@", "Admin", "chartsadmin");

        var res = await admin.PostAsync("/admin/charts/aggregate", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.GetProperty("entries").ValueKind);
    }
}
