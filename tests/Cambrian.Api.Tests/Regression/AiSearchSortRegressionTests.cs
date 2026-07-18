using System.Net;
using Cambrian.Api.Tests.Fixtures;
using Xunit;

namespace Cambrian.Api.Tests.Regression;

/// <summary>
/// Regression: AI discovery search requests the "trending" sort. The repository now resolves
/// that token through the bigint TrackStats projection instead of the legacy decimal
/// TrendingScore column, so the endpoint remains provider-safe.
/// </summary>
[Trait("Category", "Regression")]
public sealed class AiSearchSortRegressionTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public AiSearchSortRegressionTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AiSearch_TrendingSort_DoesNotServerError()
    {
        using var client = _fixture.CreateClient();

        var res = await client.GetAsync("/ai-discovery/tracks/search");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
