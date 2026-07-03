using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// F2: POST /api/newsletter correctness. Valid new email → 200 + row; duplicate → 200
/// idempotent (no second row); invalid email → 400. No path returns 503.
/// </summary>
public sealed class NewsletterControllerTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public NewsletterControllerTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Subscribe_ValidEmail_Returns200_AndStoresRow()
    {
        var client = _fixture.CreateClient();
        var email = $"news-{Guid.NewGuid():N}@example.com";

        var res = await client.PostAsJsonAsync("/api/newsletter", new { email, source = "footer" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.False(json.GetProperty("alreadySubscribed").GetBoolean());

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var row = await db.NewsletterSubscribers.SingleOrDefaultAsync(n => n.Email == email.ToLowerInvariant());
        Assert.NotNull(row);
        Assert.Equal("footer", row!.Source);
        Assert.False(row.ProviderSynced);
    }

    [Fact]
    public async Task Subscribe_DuplicateEmail_Returns200_Idempotent_NoSecondRow()
    {
        var client = _fixture.CreateClient();
        var email = $"dup-{Guid.NewGuid():N}@example.com";

        var first = await client.PostAsJsonAsync("/api/newsletter", new { email });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/newsletter", new { email });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var json = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("alreadySubscribed").GetBoolean());

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var count = await db.NewsletterSubscribers.CountAsync(n => n.Email == email.ToLowerInvariant());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Subscribe_SameEmailDifferentCase_TreatedAsDuplicate()
    {
        var client = _fixture.CreateClient();
        var local = $"case-{Guid.NewGuid():N}";

        var first = await client.PostAsJsonAsync("/api/newsletter", new { email = $"{local}@Example.com" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/newsletter", new { email = $"{local}@EXAMPLE.COM" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var json = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("alreadySubscribed").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a@b")]
    [InlineData("no-at-sign.com")]
    [InlineData("@nolocal.com")]
    [InlineData("trailing@dot.")]
    public async Task Subscribe_InvalidEmail_Returns400(string email)
    {
        var client = _fixture.CreateClient();

        var res = await client.PostAsJsonAsync("/api/newsletter", new { email });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, res.StatusCode); // never 503
    }
}
