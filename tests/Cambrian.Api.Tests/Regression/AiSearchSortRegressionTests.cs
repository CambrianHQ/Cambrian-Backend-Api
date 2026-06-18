using System.Net;
using Cambrian.Api.Tests.Fixtures;
using Xunit;

namespace Cambrian.Api.Tests.Regression;

/// <summary>
/// Regression: AI discovery search hardcodes the "trending" sort, which orders by the decimal
/// <c>TrendingScore</c>. SQLite cannot ORDER BY a decimal expression and threw a 500
/// ("SQLite does not support expressions of type 'decimal' in ORDER BY"). The sort now casts to
/// double (CAST(... AS REAL)), so the endpoint returns a controlled status on every provider.
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
